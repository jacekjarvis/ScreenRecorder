using System.Diagnostics;
using ScreenRecorder.Core.Encoding;
using ScreenRecorder.Core.Export;
using ScreenRecorder.Core.Project;

namespace ScreenRecorder.Tests;

/// <summary>
/// Builds a synthetic recording in which every frame has a different brightness, exports a
/// trimmed range, and checks the cut lands on exactly the right frames. Needs ffmpeg.
/// </summary>
public class ExportIntegrationTests : IDisposable
{
    private const int Fps = 30, Frames = 120, Size = 64;
    private readonly string _dir = Directory.CreateTempSubdirectory("sr-test").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static byte[] Ffmpeg(string args)
    {
        var p = Process.Start(new ProcessStartInfo(FFmpeg.Path, "-hide_banner -loglevel error " + args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        })!;
        using var ms = new MemoryStream();
        var err = p.StandardError.ReadToEndAsync();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, err.Result);
        return ms.ToArray();
    }

    private void MakeProject()
    {
        // Frame n has luma (n * 7) mod 256.
        Ffmpeg($"-f lavfi -i \"color=black:s={Size}x{Size}:r={Fps}:d={Frames / (double)Fps},format=yuv444p,geq=lum='mod(N*7\\,256)':cb=128:cr=128\" " +
               $"-c:v libx264 -qp 0 -g 60 -pix_fmt yuv444p \"{Path.Combine(_dir, ProjectManifest.ScreenFile)}\"");
        // 4 s of stereo audio.
        File.WriteAllBytes(Path.Combine(_dir, ProjectManifest.SystemAudioFile), new byte[48000 * 4 * Frames / Fps]);
        new ProjectManifest
        {
            Width = Size, Height = Size, Fps = Fps, Frames = Frames, DurationSeconds = Frames / (double)Fps, Completed = true,
            SystemAudio = new TrackInfo(ProjectManifest.SystemAudioFile, "test", 48000, 2),
        }.Save(_dir);
    }

    /// <summary>Average brightness of each frame in a video file.</summary>
    private static double[] FrameBrightness(string file)
    {
        var raw = Ffmpeg($"-i \"{file}\" -f rawvideo -pix_fmt gray -");
        int n = raw.Length / (Size * Size);
        var result = new double[n];
        for (int i = 0; i < n; i++)
        {
            long sum = 0;
            for (int j = 0; j < Size * Size; j++) sum += raw[i * Size * Size + j];
            result[i] = (double)sum / (Size * Size);
        }
        return result;
    }

    [Fact]
    public async Task Trimmed_export_contains_exactly_the_chosen_frames()
    {
        if (!FFmpeg.IsAvailable) return;
        MakeProject();
        var source = FrameBrightness(Path.Combine(_dir, ProjectManifest.ScreenFile));
        Assert.Equal(Frames, source.Length);

        string output = Path.Combine(_dir, "out.mp4");
        await Mp4Exporter.ExportAsync(_dir, output,
            new ExportOptions { StartFrame = 30, EndFrame = 59, UseHardwareEncoder = false, IncludeCursor = false });

        var exported = FrameBrightness(output);
        Assert.Equal(30, exported.Length);
        Assert.Equal(source[30], exported[0], 0);
        Assert.Equal(source[59], exported[^1], 0);
        // Neighbouring frames differ by about 7 levels, so a one-frame slip would fail here.
        Assert.True(Math.Abs(exported[0] - source[29]) > 3);
        Assert.True(Math.Abs(exported[^1] - source[60]) > 3);

        // 30 frames at 30 fps: the audio must be trimmed to the same 1 second.
        string ffprobe = Path.Combine(Path.GetDirectoryName(FFmpeg.Path)!, "ffprobe.exe");
        if (File.Exists(ffprobe))
        {
            var p = Process.Start(new ProcessStartInfo(ffprobe,
                $"-v error -select_streams a:0 -show_entries stream=duration -of csv=p=0 \"{output}\"")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            double audioSeconds = double.Parse(p.StandardOutput.ReadToEnd().Trim(), System.Globalization.CultureInfo.InvariantCulture);
            p.WaitForExit();
            Assert.InRange(audioSeconds, 0.95, 1.05);
        }
    }

    [Fact]
    public async Task Preview_returns_the_requested_frame()
    {
        if (!FFmpeg.IsAvailable) return;
        MakeProject();
        var source = FrameBrightness(Path.Combine(_dir, ProjectManifest.ScreenFile));
        var preview = new FramePreview(_dir);
        var frame = await preview.GetAsync(45, Size, Size);
        Assert.NotNull(frame);
        // BGRA of a grey frame: compare the green channel to the source luma.
        double avg = 0;
        for (int i = 0; i < frame!.Width * frame.Height; i++) avg += frame.Bgra[i * 4 + 1];
        avg /= frame.Width * frame.Height;
        Assert.InRange(avg, source[45] - 3, source[45] + 3);
        Assert.True(Math.Abs(avg - source[44]) > 3);
    }
}
