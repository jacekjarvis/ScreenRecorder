using System.Diagnostics;
using System.Text;

namespace ScreenRecorder.Core.Encoding;

/// <summary>
/// Pipes raw BGRA frames into an ffmpeg process. Frames are written at a constant rate,
/// so frame N is always at N / fps seconds.
/// </summary>
public sealed class RawVideoEncoder : IDisposable
{
    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly StringBuilder _log = new();
    private bool _closed;

    public int Width { get; }
    public int Height { get; }
    public string OutputPath { get; }
    public long FramesWritten { get; private set; }

    public RawVideoEncoder(int width, int height, double fps, string outputPath, string videoArgs, string extraInputsAndOutputArgs = "")
    {
        Width = width;
        Height = height;
        OutputPath = outputPath;
        string fpsText = fps.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string args = $"-hide_banner -y -loglevel error -f rawvideo -pix_fmt bgra -s {width}x{height} -r {fpsText} -i pipe:0 " +
                      $"{extraInputsAndOutputArgs} {videoArgs} \"{outputPath}\"";
        _process = Process.Start(new ProcessStartInfo(FFmpeg.Path, args)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        }) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        _process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            lock (_log) { if (_log.Length < 16_000) _log.AppendLine(e.Data); }
        };
        _process.BeginErrorReadLine();
        _stdin = _process.StandardInput.BaseStream;
    }

    public void Write(ReadOnlySpan<byte> frame)
    {
        if (_closed) return;
        if (frame.Length != Width * Height * 4) throw new ArgumentException("Frame size does not match the encoder.");
        try
        {
            _stdin.Write(frame);
            FramesWritten++;
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"ffmpeg stopped accepting frames: {Log}", ex);
        }
    }

    public string Log { get { lock (_log) return _log.ToString(); } }

    /// <summary>Close the pipe and wait for ffmpeg to finish writing the file.</summary>
    public void Complete(TimeSpan? timeout = null)
    {
        if (_closed) return;
        _closed = true;
        try { _stdin.Flush(); _stdin.Dispose(); } catch (IOException) { }
        if (!_process.WaitForExit(timeout ?? TimeSpan.FromMinutes(2)))
        {
            try { _process.Kill(true); } catch { }
            throw new TimeoutException("ffmpeg did not finish in time.");
        }
        _process.WaitForExit();
        if (_process.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg failed ({_process.ExitCode}): {Log}");
    }

    public void Dispose()
    {
        try { Complete(TimeSpan.FromSeconds(30)); } catch { }
        _process.Dispose();
    }
}
