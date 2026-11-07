using System.Diagnostics;

namespace ScreenRecorder.Core.Encoding;

public enum VideoEncoderKind { Nvidia, Intel, Amd, Software }

public enum VideoQuality { Highest, High, Medium, Small }

public enum AudioQuality { Highest, High, Medium, Low }

/// <summary>Finds ffmpeg and picks the fastest working H.264 encoder on this PC.</summary>
public static class FFmpeg
{
    private static string? _path;
    private static VideoEncoderKind? _encoder;
    private static readonly object Gate = new();

    public static string Path => _path ??= Locate() ?? throw new FileNotFoundException(
        "FFmpeg was not found. Put ffmpeg.exe next to ScreenRecorder.exe or install it (winget install Gyan.FFmpeg).");

    public static bool IsAvailable => (_path ??= Locate()) != null;

    private static string? Locate()
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("SCREENRECORDER_FFMPEG"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(System.IO.Path.Combine(dir.Trim('"'), "ffmpeg.exe"));
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(c));
    }

    /// <summary>Hardware encoders are tried first; each is test-encoded because a listed encoder may still lack a GPU.</summary>
    public static VideoEncoderKind BestEncoder
    {
        get
        {
            lock (Gate)
            {
                if (_encoder is { } e) return e;
                foreach (var kind in new[] { VideoEncoderKind.Nvidia, VideoEncoderKind.Intel, VideoEncoderKind.Amd })
                {
                    if (Probe(kind)) return (_encoder = kind).Value;
                }
                return (_encoder = VideoEncoderKind.Software).Value;
            }
        }
    }

    private static bool Probe(VideoEncoderKind kind)
    {
        try
        {
            var args = $"-hide_banner -v error -f lavfi -i color=black:s=640x480:r=30:d=0.2 {RecordingArgs(kind)} -f null -";
            var r = Run(args, TimeSpan.FromSeconds(15));
            return r.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>Recording favours speed and fidelity; the file is re-encoded on export. Keyframes every 2 s keep seeking fast.</summary>
    public static string RecordingArgs(VideoEncoderKind kind) => CodecArgs(kind, 18, realtime: true) + " -g 60";

    public static string ExportArgs(VideoEncoderKind kind, VideoQuality quality) =>
        CodecArgs(kind, quality switch
        {
            VideoQuality.Highest => 16,
            VideoQuality.High => 20,
            VideoQuality.Medium => 24,
            _ => 28,
        }, realtime: false) + " -movflags +faststart";

    /// <summary>Constant-quality H.264 arguments; lower <paramref name="q"/> = better quality, bigger file.</summary>
    private static string CodecArgs(VideoEncoderKind kind, int q, bool realtime) => kind switch
    {
        VideoEncoderKind.Nvidia => $"-c:v h264_nvenc -preset {(realtime ? "p3" : "p5")} -rc vbr -cq {q} -b:v 0 -pix_fmt yuv420p",
        VideoEncoderKind.Intel => $"-c:v h264_qsv -preset {(realtime ? "faster" : "slow")} -global_quality {q} -pix_fmt nv12",
        VideoEncoderKind.Amd => $"-c:v h264_amf -quality {(realtime ? "speed" : "quality")} -rc cqp -qp_i {q} -qp_p {q + 2} -pix_fmt yuv420p",
        _ => $"-c:v libx264 -preset {(realtime ? "ultrafast" : "faster")} -crf {q} -pix_fmt yuv420p",
    };

    public sealed record RunResult(int ExitCode, string StdOut, string StdErr);

    public static RunResult Run(string args, TimeSpan timeout)
    {
        using var p = Process.Start(new ProcessStartInfo(Path, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeout))
        {
            try { p.Kill(true); } catch { }
            return new RunResult(-1, "", "timed out");
        }
        return new RunResult(p.ExitCode, stdout.Result, stderr.Result);
    }
}
