using System.Diagnostics;
using ScreenRecorder.Core.Encoding;
using ScreenRecorder.Core.Export;
using ScreenRecorder.Core.Project;

namespace ScreenRecorder.Core.Playback;

/// <summary>
/// Plays a range of the recording at preview size: decodes with ffmpeg, draws the cursor, and
/// hands each frame over at its proper time. Frames arrive on a background thread.
/// </summary>
public sealed class VideoPlayback : IDisposable
{
    private readonly string _projectDir;
    private readonly ProjectManifest _m;
    private readonly CursorTrack _cursor;
    private readonly int _w, _h;
    private readonly long _first, _last;
    private readonly Action<long, byte[]> _onFrame;
    private Process? _ffmpeg;
    private Thread? _thread;
    private volatile bool _stop;

    /// <summary>Raised on the playback thread when the last frame has been shown (not when stopped).</summary>
    public event Action? Ended;

    public VideoPlayback(string projectDir, ProjectManifest manifest, CursorTrack cursor, int width, int height,
        long firstFrame, long lastFrame, Action<long, byte[]> onFrame)
    {
        _projectDir = projectDir;
        _m = manifest;
        _cursor = cursor;
        _w = width;
        _h = height;
        _first = firstFrame;
        _last = lastFrame;
        _onFrame = onFrame;
    }

    public void Start()
    {
        long count = _last - _first + 1;
        if (count <= 0) { Ended?.Invoke(); return; }
        string seek = Mp4Exporter.SeekSeconds(_first, _m.Fps).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        _ffmpeg = Process.Start(new ProcessStartInfo(FFmpeg.Path,
            $"-hide_banner -loglevel error -ss {seek} -i \"{Path.Combine(_projectDir, ProjectManifest.ScreenFile)}\" " +
            $"-frames:v {count} -vf scale={_w}:{_h}:flags=bilinear -f rawvideo -pix_fmt bgra -")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        _ffmpeg.ErrorDataReceived += (_, _) => { };
        _ffmpeg.BeginErrorReadLine();
        _thread = new Thread(Run) { IsBackground = true, Name = "Playback" };
        _thread.Start();
    }

    private void Run()
    {
        var stream = _ffmpeg!.StandardOutput.BaseStream;
        var buffers = new[] { new byte[_w * _h * 4], new byte[_w * _h * 4] };
        var clock = Stopwatch.StartNew();
        double scale = (double)_w / _m.Width;
        bool completed = false;
        try
        {
            for (long i = 0; !_stop; i++)
            {
                var buf = buffers[i % 2];
                if (stream.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false) < buf.Length) { completed = true; break; }
                long frame = _first + i;
                _cursor.Draw(frame, buf, _w, _h, scale);
                double due = i / _m.Fps, now = clock.Elapsed.TotalSeconds;
                if (due > now) Thread.Sleep(TimeSpan.FromSeconds(due - now));
                if (_stop) break;
                _onFrame(frame, buf);
                if (frame >= _last) { completed = true; break; }
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        if (completed && !_stop) Ended?.Invoke();
    }

    public void Dispose()
    {
        _stop = true;
        try { if (_ffmpeg is { HasExited: false }) _ffmpeg.Kill(true); } catch { }
        if (_thread != null && _thread != Thread.CurrentThread) _thread.Join(TimeSpan.FromSeconds(2));
        _ffmpeg?.Dispose();
    }
}
