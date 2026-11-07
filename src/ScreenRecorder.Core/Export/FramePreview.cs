using System.Diagnostics;
using System.Globalization;
using ScreenRecorder.Core.Encoding;
using ScreenRecorder.Core.Project;

namespace ScreenRecorder.Core.Export;

/// <summary>Grabs single frames from a recording (with the cursor) for the trim preview.</summary>
public sealed class FramePreview
{
    private readonly string _projectDir;
    private readonly ProjectManifest _manifest;
    private readonly CursorTrack _cursor;

    public long FrameCount { get; }
    public double Fps => _manifest.Fps;
    public ProjectManifest Manifest => _manifest;

    public FramePreview(string projectDir)
    {
        _projectDir = projectDir;
        _manifest = ProjectManifest.Load(projectDir);
        _cursor = CursorTrack.Load(projectDir);
        FrameCount = Mp4Exporter.FrameCount(projectDir, _manifest);
    }

    public sealed record Frame(int Width, int Height, byte[] Bgra);

    /// <summary>Returns frame <paramref name="index"/> scaled to fit within <paramref name="maxWidth"/> x <paramref name="maxHeight"/>.</summary>
    public async Task<Frame?> GetAsync(long index, int maxWidth, int maxHeight, CancellationToken cancel = default)
    {
        index = Math.Clamp(index, 0, Math.Max(0, FrameCount - 1));
        double scale = Math.Min(1.0, Math.Min((double)maxWidth / _manifest.Width, (double)maxHeight / _manifest.Height));
        int w = Math.Max(2, (int)(_manifest.Width * scale) & ~1);
        int h = Math.Max(2, (int)(_manifest.Height * scale) & ~1);
        string seek = Mp4Exporter.SeekSeconds(index, Fps).ToString("0.######", CultureInfo.InvariantCulture);
        using var p = Process.Start(new ProcessStartInfo(FFmpeg.Path,
            $"-hide_banner -loglevel error -ss {seek} -i \"{Path.Combine(_projectDir, ProjectManifest.ScreenFile)}\" " +
            $"-frames:v 1 -vf scale={w}:{h}:flags=bilinear -f rawvideo -pix_fmt bgra -")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        using var reg = cancel.Register(() => { try { p.Kill(true); } catch { } });
        _ = p.StandardError.ReadToEndAsync(cancel);
        var buf = new byte[w * h * 4];
        int got;
        try { got = await p.StandardOutput.BaseStream.ReadAtLeastAsync(buf, buf.Length, false, cancel); }
        catch (OperationCanceledException) { return null; }
        await p.WaitForExitAsync(CancellationToken.None);
        if (got < buf.Length) return null;
        _cursor.Draw(index, buf, w, h, (double)w / _manifest.Width);
        return new Frame(w, h, buf);
    }
}
