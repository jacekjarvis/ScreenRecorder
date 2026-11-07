using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using ScreenRecorder.Core.Encoding;
using ScreenRecorder.Core.Project;

namespace ScreenRecorder.Core.Export;

/// <summary>
/// Turns a project folder into an MP4: decodes the screen video, draws the cursor back in,
/// overlays the webcam, mixes the audio tracks, and trims to the chosen frame range.
/// </summary>
public static class Mp4Exporter
{
    private static string Inv(double v) => v.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Number of frames in the recording (also works for a recording that was cut short by a crash).</summary>
    public static long FrameCount(string projectDir, ProjectManifest manifest)
    {
        if (manifest.Completed && manifest.Frames > 0) return manifest.Frames;
        long lines = 0;
        string cursor = Path.Combine(projectDir, ProjectManifest.CursorFile);
        if (File.Exists(cursor))
        {
            using var fs = new FileStream(cursor, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var r = new StreamReader(fs);
            while (r.ReadLine() != null) lines++;
        }
        return Math.Max(manifest.Frames, lines);
    }

    /// <summary>The time ffmpeg should seek to so that <paramref name="frame"/> is the first one output.</summary>
    internal static double SeekSeconds(long frame, double fps) => frame <= 0 ? 0 : (frame - 0.5) / fps;

    public static Task ExportAsync(string projectDir, string outputPath, ExportOptions options,
        IProgress<ExportProgress>? progress = null, CancellationToken cancel = default) =>
        Task.Run(() => Export(projectDir, outputPath, options, progress, cancel), cancel);

    private static void Export(string projectDir, string outputPath, ExportOptions o, IProgress<ExportProgress>? progress, CancellationToken cancel)
    {
        var m = ProjectManifest.Load(projectDir);
        long total = FrameCount(projectDir, m);
        if (total <= 0) throw new InvalidOperationException("This recording has no frames.");
        long first = Math.Clamp(o.StartFrame, 0, total - 1);
        long last = Math.Clamp(o.EndFrame ?? total - 1, first, total - 1);
        long count = last - first + 1;
        double fps = m.Fps;
        double startSec = first / fps, durSec = count / fps;
        int w = m.Width, h = m.Height;

        var cursor = o.IncludeCursor ? CursorTrack.Load(projectDir) : CursorTrack.Empty;
        string args = BuildEncoderInputs(projectDir, m, o, startSec, durSec, w);
        var kind = o.UseHardwareEncoder ? FFmpeg.BestEncoder : VideoEncoderKind.Software;
        string videoArgs = FFmpeg.ExportArgs(kind, o.VideoQuality);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        string temp = outputPath + ".part.mp4";
        var sw = Stopwatch.StartNew();
        using var decoder = Process.Start(new ProcessStartInfo(FFmpeg.Path,
            $"-hide_banner -loglevel error -ss {Inv(SeekSeconds(first, fps))} -i \"{Path.Combine(projectDir, ProjectManifest.ScreenFile)}\" " +
            $"-frames:v {count} -f rawvideo -pix_fmt bgra -")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var decoderErrors = new StringBuilder();
        decoder.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (decoderErrors) decoderErrors.AppendLine(e.Data); };
        decoder.BeginErrorReadLine();

        var encoder = new RawVideoEncoder(w, h, fps, temp, videoArgs, args);
        int frameBytes = w * h * 4;
        // Read and write on separate threads so decoding and encoding overlap.
        using var free = new BlockingCollection<byte[]>(4);
        using var filled = new BlockingCollection<(byte[] Buf, long Index)>(4);
        for (int i = 0; i < 4; i++) free.Add(new byte[frameBytes]);
        Exception? readError = null;
        var reader = new Thread(() =>
        {
            try
            {
                var stream = decoder.StandardOutput.BaseStream;
                for (long i = 0; i < count && !cancel.IsCancellationRequested; i++)
                {
                    var buf = free.Take(cancel);
                    int got = stream.ReadAtLeast(buf, frameBytes, throwOnEndOfStream: false);
                    if (got < frameBytes) { free.Add(buf); break; }
                    filled.Add((buf, i), cancel);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { readError = ex; }
            finally { filled.CompleteAdding(); }
        }) { IsBackground = true, Name = "Export decode" };
        reader.Start();

        bool ok = false;
        try
        {
            long done = 0;
            foreach (var (buf, i) in filled.GetConsumingEnumerable(cancel))
            {
                cursor.Draw(first + i, buf, w, h);
                encoder.Write(buf);
                free.Add(buf, cancel);
                done++;
                if (done % 15 == 0) progress?.Report(new ExportProgress(done, count, sw.Elapsed));
            }
            reader.Join();
            if (readError != null) throw new InvalidOperationException($"Reading the recording failed: {readError.Message}", readError);
            if (done == 0) throw new InvalidOperationException($"The recording could not be read. {decoderErrors}");
            encoder.Complete(TimeSpan.FromMinutes(30));
            progress?.Report(new ExportProgress(count, count, sw.Elapsed));
            File.Move(temp, outputPath, overwrite: true);
            ok = true;
        }
        finally
        {
            if (!decoder.HasExited) { try { decoder.Kill(true); } catch { } }
            encoder.Dispose();
            if (!ok) { try { File.Delete(temp); } catch { } }
        }
    }

    /// <summary>Extra ffmpeg inputs (audio, webcam) and the filter graph that combines them with the screen.</summary>
    internal static string BuildEncoderInputs(string projectDir, ProjectManifest m, ExportOptions o, double startSec, double durSec, int width)
    {
        var inputs = new StringBuilder();
        var filters = new List<string>();
        var audioLabels = new List<string>();
        int index = 1; // input 0 is the screen frames on stdin
        string trim = $"-ss {Inv(startSec)} -t {Inv(durSec)}";

        void AddAudio(TrackInfo? t, bool include)
        {
            if (t == null || !include) return;
            string file = Path.Combine(projectDir, t.File);
            if (!File.Exists(file) || new FileInfo(file).Length == 0) return;
            inputs.Append($"-f s16le -ar {t.SampleRate} -ac {t.Channels} {trim} -i \"{file}\" ");
            audioLabels.Add($"[{index}:a]");
            index++;
        }
        AddAudio(m.SystemAudio, o.IncludePcSounds);
        AddAudio(m.Microphone, o.IncludeMicrophone);

        string videoOut = "0:v";
        if (m.Webcam != null && o.IncludeWebcam && File.Exists(Path.Combine(projectDir, m.Webcam.File)))
        {
            inputs.Append($"{trim} -i \"{Path.Combine(projectDir, m.Webcam.File)}\" ");
            int camWidth = Math.Max(2, (int)(width * 0.22) & ~1);
            filters.Add($"[{index}:v]scale={camWidth}:-2[cam]");
            filters.Add("[0:v][cam]overlay=W-w-16:H-h-16:eof_action=pass[v]");
            videoOut = "[v]";
            index++;
        }

        string audioOut = "";
        if (audioLabels.Count == 1) audioOut = audioLabels[0].Trim('[', ']');
        else if (audioLabels.Count > 1)
        {
            filters.Add($"{string.Concat(audioLabels)}amix=inputs={audioLabels.Count}:duration=longest:normalize=0[a]");
            audioOut = "[a]";
        }

        var sb = new StringBuilder(inputs.ToString());
        if (filters.Count > 0) sb.Append($"-filter_complex \"{string.Join(';', filters)}\" ");
        sb.Append($"-map {videoOut} ");
        if (audioOut.Length > 0)
            sb.Append($"-map {audioOut} -c:a aac -b:a {ExportOptions.AudioBitrateKbps(o.AudioQuality)}k -ar 48000 ");
        return sb.ToString();
    }
}
