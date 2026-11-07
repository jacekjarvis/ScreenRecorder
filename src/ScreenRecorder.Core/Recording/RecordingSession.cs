using ScreenRecorder.Core.Audio;
using ScreenRecorder.Core.Capture;
using ScreenRecorder.Core.Encoding;
using ScreenRecorder.Core.Input;
using ScreenRecorder.Core.Project;
using ScreenRecorder.Core.Timing;
using ScreenRecorder.Core.Webcam;

namespace ScreenRecorder.Core.Recording;

public enum RecordingState { Recording, Paused, Stopping, Stopped }

/// <summary>
/// One recording in progress. Writes a project folder: screen video (no cursor), each audio source
/// on its own track, webcam video, cursor/mouse/keyboard data, markers and notes.
/// </summary>
public sealed class RecordingSession : IAsyncDisposable
{
    private readonly RecordingOptions _options;
    private readonly ScreenCapture _capture;
    private readonly RawVideoEncoder _screenEncoder;
    private readonly FramePacer _screenPacer;
    private readonly InputRecorder _input;
    private readonly List<AudioTrackRecorder> _audio = new();
    private readonly AudioTrackRecorder? _pcSounds, _mic;
    private WebcamCapture? _webcam;
    private RawVideoEncoder? _webcamEncoder;
    private FramePacer? _webcamPacer;
    private readonly Timer _monitor;
    private readonly object _gate = new();
    private bool _limitRaised;
    private bool _failedRaised;

    public string ProjectDir { get; }
    public ProjectManifest Manifest { get; }
    public RecordingClock Clock { get; } = new();
    public RecordingState State { get; private set; } = RecordingState.Recording;
    public TimeSpan Elapsed => Clock.Elapsed;
    public long SizeBytes { get; private set; }
    public float PcSoundsLevel => _pcSounds?.Level ?? 0;
    public float MicrophoneLevel => _mic?.Level ?? 0;

    /// <summary>Raised (once, on a background thread) when the size limit is reached.</summary>
    public event Action? SizeLimitReached;
    /// <summary>Raised (once) when less than <see cref="MinimumFreeBytes"/> is left on the recording drive.</summary>
    public event Action? DiskAlmostFull;
    public const long MinimumFreeBytes = 2L * 1024 * 1024 * 1024;
    private bool _diskRaised;
    /// <summary>Raised (once, on a background thread) if recording can't continue, e.g. the recorded window closed.</summary>
    public event Action<string>? Failed;

    private RecordingSession(RecordingOptions options, string projectDir)
    {
        _options = options;
        ProjectDir = projectDir;
        Directory.CreateDirectory(projectDir);

        _capture = new ScreenCapture(options.Target);
        var bounds = _capture.Bounds;
        Manifest = new ProjectManifest
        {
            CaptureMode = options.Target.Mode,
            CaptureDescription = options.Target.Description,
            CaptureBounds = bounds,
            Width = _capture.Width,
            Height = _capture.Height,
            Fps = options.Fps,
            Encoder = FFmpeg.BestEncoder.ToString(),
            HasKeystrokes = options.CaptureKeystrokes,
        };

        _screenEncoder = new RawVideoEncoder(_capture.Width, _capture.Height, options.Fps,
            Path.Combine(projectDir, ProjectManifest.ScreenFile), FFmpeg.RecordingArgs(FFmpeg.BestEncoder));

        if (options.RecordPcSounds)
            _pcSounds = TryAudio(options.PcSoundsDeviceId, pcSounds: true, ProjectManifest.SystemAudioFile, t => Manifest.SystemAudio = t);
        if (options.RecordMicrophone)
            _mic = TryAudio(options.MicrophoneDeviceId, pcSounds: false, ProjectManifest.MicFile, t => Manifest.Microphone = t);

        _input = new InputRecorder(projectDir, Clock, _capture.CurrentOrigin, options.CaptureKeystrokes, options.MaskPasswords);

        var frame = new byte[_capture.Width * _capture.Height * 4];
        _screenPacer = new FramePacer(Clock, options.Fps, f =>
        {
            _capture.CopyLatest(frame);
            _input.SampleCursor(f);
            _screenEncoder.Write(frame);
        }, "Screen frames");
        _screenPacer.Failed += ex => RaiseFailed($"Screen recording stopped: {ex.Message}");
        _capture.TargetClosed += () => RaiseFailed("The window being recorded was closed.");

        _monitor = new Timer(_ => CheckSize(), null, Timeout.Infinite, Timeout.Infinite);
    }

    private AudioTrackRecorder? TryAudio(string? deviceId, bool pcSounds, string file, Action<TrackInfo> record)
    {
        try
        {
            var r = new AudioTrackRecorder(deviceId, pcSounds, Path.Combine(ProjectDir, file), Clock);
            _audio.Add(r);
            record(new TrackInfo(file, r.DeviceName, r.SampleRate, r.Channels));
            return r;
        }
        catch (Exception ex)
        {
            Manifest.Warnings.Add($"{(pcSounds ? "PC sounds" : "Microphone")} could not be recorded: {ex.Message}");
            return null;
        }
    }

    public static async Task<RecordingSession> StartAsync(RecordingOptions options, string projectDir)
    {
        var s = new RecordingSession(options, projectDir);
        try
        {
            await s.StartCoreAsync();
            return s;
        }
        catch
        {
            await s.DisposeAsync();
            throw;
        }
    }

    private async Task StartCoreAsync()
    {
        if (_options.RecordWebcam)
        {
            try
            {
                _webcam = await WebcamCapture.OpenAsync(_options.WebcamDeviceId);
                _webcamEncoder = new RawVideoEncoder(_webcam.Width, _webcam.Height, _webcam.Fps,
                    Path.Combine(ProjectDir, ProjectManifest.WebcamFile), FFmpeg.RecordingArgs(FFmpeg.BestEncoder));
                var camFrame = new byte[_webcam.Width * _webcam.Height * 4];
                var cam = _webcam;
                var enc = _webcamEncoder;
                _webcamPacer = new FramePacer(Clock, _webcam.Fps, _ => { cam.CopyLatest(camFrame); enc.Write(camFrame); }, "Webcam frames");
                _webcamPacer.Failed += ex => Manifest.Warnings.Add($"Webcam recording stopped: {ex.Message}");
                Manifest.Webcam = new WebcamTrack(ProjectManifest.WebcamFile, _webcam.Name, _webcam.Width, _webcam.Height, _webcam.Fps);
            }
            catch (Exception ex)
            {
                Manifest.Warnings.Add($"Webcam could not be recorded: {ex.Message}");
                _webcamEncoder?.Dispose();
                _webcamEncoder = null;
                if (_webcam != null) await _webcam.DisposeAsync();
                _webcam = null;
            }
        }

        _capture.Start();
        foreach (var a in _audio) a.Start();
        _input.Start();

        // Give each capture source a moment to deliver its first image so the movie doesn't open on black.
        var deadline = DateTime.UtcNow.AddSeconds(1);
        while (_capture.FramesArrived == 0 && DateTime.UtcNow < deadline) await Task.Delay(15);

        Clock.Start();
        _screenPacer.Start();
        _webcamPacer?.Start();
        Manifest.Save(ProjectDir);
        _monitor.Change(1000, 1000);
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (State != RecordingState.Recording) return;
            Clock.Pause();
            State = RecordingState.Paused;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (State != RecordingState.Paused) return;
            Clock.Resume();
            State = RecordingState.Recording;
        }
    }

    public void AddMarker(string label, double? atSeconds = null)
    {
        lock (_gate) Manifest.Markers.Add(new Marker(atSeconds ?? Clock.Elapsed.TotalSeconds, label));
    }

    public void AddNote(string text, double atSeconds)
    {
        lock (_gate) Manifest.Notes.Add(new Note(atSeconds, text));
    }

    private void CheckSize()
    {
        try
        {
            SizeBytes = new DirectoryInfo(ProjectDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            _input.Flush();
            if (_options.SizeLimitBytes is { } limit && SizeBytes >= limit && !_limitRaised)
            {
                _limitRaised = true;
                SizeLimitReached?.Invoke();
            }
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(ProjectDir))!);
            if (drive.AvailableFreeSpace < MinimumFreeBytes && !_diskRaised)
            {
                _diskRaised = true;
                Manifest.Warnings.Add("Recording stopped because the disk was almost full.");
                DiskAlmostFull?.Invoke();
            }
        }
        catch { }
    }

    private void RaiseFailed(string message)
    {
        lock (_gate)
        {
            if (_failedRaised || State is RecordingState.Stopping or RecordingState.Stopped) return;
            _failedRaised = true;
        }
        Failed?.Invoke(message);
    }

    /// <summary>Finishes every track and writes the final project.json.</summary>
    public async Task<ProjectManifest> StopAsync()
    {
        lock (_gate)
        {
            if (State is RecordingState.Stopping or RecordingState.Stopped) return Manifest;
            State = RecordingState.Stopping;
        }
        _monitor.Change(Timeout.Infinite, Timeout.Infinite);
        Clock.Pause();

        await Task.Run(() =>
        {
            _screenPacer.Stop();
            _webcamPacer?.Stop();
            long frames = _screenPacer.FramesProduced;
            var duration = TimeSpan.FromSeconds(frames / _options.Fps);
            foreach (var a in _audio)
            {
                try { a.Finish(duration); }
                catch (Exception ex) { Manifest.Warnings.Add($"Audio track {a.Path} may be incomplete: {ex.Message}"); }
            }
            _input.Dispose();
            _capture.Dispose();
            try { _screenEncoder.Complete(); }
            catch (Exception ex) { Manifest.Warnings.Add($"Screen video may be incomplete: {ex.Message}"); }
            if (_webcamEncoder != null)
            {
                try { _webcamEncoder.Complete(); }
                catch (Exception ex) { Manifest.Warnings.Add($"Webcam video may be incomplete: {ex.Message}"); }
            }
            Manifest.Frames = frames;
            Manifest.DurationSeconds = duration.TotalSeconds;
        });
        if (_webcam != null) await _webcam.DisposeAsync();
        _webcam = null;

        lock (_gate)
        {
            Manifest.Markers.Sort((a, b) => a.Time.CompareTo(b.Time));
            Manifest.Notes.Sort((a, b) => a.Time.CompareTo(b.Time));
            Manifest.Completed = true;
            Manifest.Save(ProjectDir);
            State = RecordingState.Stopped;
        }
        return Manifest;
    }

    public async ValueTask DisposeAsync()
    {
        if (State != RecordingState.Stopped)
        {
            try { await StopAsync(); } catch { }
        }
        await _monitor.DisposeAsync();
        foreach (var a in _audio) a.Dispose();
        _screenEncoder.Dispose();
        _webcamEncoder?.Dispose();
    }
}
