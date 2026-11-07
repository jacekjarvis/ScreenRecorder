using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using ScreenRecorder.App.Views;
using ScreenRecorder.Core.Capture;
using ScreenRecorder.Core.Desktop;
using ScreenRecorder.Core.Interop;
using ScreenRecorder.Core.Recording;

namespace ScreenRecorder.App;

public enum RecorderState { Idle, Scheduled, Starting, Recording, Paused, Stopping }

/// <summary>Owns the current recording and everything that happens around it.</summary>
public sealed class RecorderController
{
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Native.POINT p);

    private readonly DispatcherTimer _tick;
    private RecordingSession? _session;
    private bool _iconsHidden;
    private NoteWindow? _note;
    private DateTime _cursorAtEdgeSince = DateTime.MaxValue;
    private DateTime? _scheduledStop;
    private int _ticks;

    public AppSettings Settings { get; }
    public RecorderState State { get; private set; } = RecorderState.Idle;
    public TimeSpan Elapsed => _session?.Elapsed ?? TimeSpan.Zero;
    public long SizeBytes => _session?.SizeBytes ?? 0;
    public RecordingSession? Session => _session;

    /// <summary>The window chosen with "click the window to record".</summary>
    public WindowInfo? PickedWindow { get; set; }
    /// <summary>The red region frame (when Area = Region).</summary>
    public RegionFrameWindow? RegionFrame { get; set; }

    public event Action? StateChanged;
    public event Action? Tick;
    /// <summary>The red frame was shown, moved, resized or closed.</summary>
    public event Action? RegionFrameChanged;

    /// <summary>Shows the movable red region frame at the last region used (or a sensible default).</summary>
    public void ShowRegionFrame()
    {
        CloseRegionFrame();
        // A new or lost region goes in the middle of the screen you're using (where the mouse is).
        GetCursorPos(out var p);
        var screen = Displays.MonitorFor(new PixelRect(p.X, p.Y, 1, 1)).Bounds;
        var middle = new PixelRect(screen.X + screen.Width / 6, screen.Y + screen.Height / 6, screen.Width * 2 / 3, screen.Height * 2 / 3);
        var r = Displays.KeepOnScreen(Settings.LastRegion ?? default, middle);
        var frame = new RegionFrameWindow(r, locked: false);
        frame.RegionChanged += () => RegionFrameChanged?.Invoke();
        RegionFrame = frame;
        frame.Show();
        RegionFrameChanged?.Invoke();
    }

    /// <summary>Shows a fixed red frame around a picked window.</summary>
    public void ShowWindowFrame(IntPtr hwnd)
    {
        CloseRegionFrame();
        RegionFrame = new RegionFrameWindow(Displays.GetWindowBounds(hwnd), locked: true);
        RegionFrame.Show();
        RegionFrameChanged?.Invoke();
    }

    /// <summary>Removes the red frame (remembering a region's position for next time).</summary>
    public void CloseRegionFrame()
    {
        if (RegionFrame == null) return;
        if (!RegionFrame.IsLocked)
        {
            Settings.LastRegion = RegionFrame.RegionPixels;
            Settings.Save();
        }
        RegionFrame.Close();
        RegionFrame = null;
        RegionFrameChanged?.Invoke();
    }

    public RecorderController(AppSettings settings)
    {
        Settings = settings;
        _tick = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Normal, (_, _) => OnTick(), Dispatcher.CurrentDispatcher);
        _tick.Start();
    }

    private void SetState(RecorderState s)
    {
        State = s;
        StateChanged?.Invoke();
    }

    public bool IsBusy => State is RecorderState.Recording or RecorderState.Paused or RecorderState.Starting or RecorderState.Stopping;

    // ---------------- start / schedule ----------------

    /// <summary>Record button: honours "Set Timer" scheduling, otherwise starts now.</summary>
    public async Task RecordAsync()
    {
        if (State == RecorderState.Scheduled) { CancelSchedule(); return; }
        if (IsBusy) return;
        if (Settings.ScheduleEnabled)
        {
            if (Settings.ScheduleStart > DateTime.Now)
            {
                SetState(RecorderState.Scheduled);
                Dialogs.Info($"The next recording is scheduled to start at {Settings.ScheduleStart:t} on {Settings.ScheduleStart:d}.\n\nLeave Jarvo Screen Recorder running.", "Scheduled recording");
                return;
            }
            await StartScheduledAsync();
            return;
        }
        await StartNowAsync();
    }

    public void CancelSchedule()
    {
        if (State == RecorderState.Scheduled) SetState(RecorderState.Idle);
    }

    private async Task StartScheduledAsync()
    {
        _scheduledStop = Settings.ScheduleStopByDuration
            ? DateTime.Now.AddMinutes(Math.Max(1, Settings.ScheduleDurationMinutes))
            : Settings.ScheduleStop;
        // Like FlashBack: after the scheduled recording runs, scheduling switches itself off.
        Settings.ScheduleEnabled = false;
        Settings.Save();
        SetState(RecorderState.Idle);
        await StartNowAsync();
    }

    public CaptureTarget? BuildTarget()
    {
        var monitors = Displays.GetMonitors();
        switch (Settings.Area)
        {
            case AreaKind.AllMonitors:
                return CaptureTarget.AllMonitors();
            case AreaKind.Window:
                if (PickedWindow is { } w && Native.IsWindow(w.Handle) && !Native.IsIconic(w.Handle))
                    return new CaptureTarget.Window(w.Handle, w.Title);
                return null;
            case AreaKind.Region:
                var r = RegionFrame is { IsLocked: false } f ? f.RegionPixels : Settings.LastRegion;
                return r is { IsEmpty: false } rr ? CaptureTarget.Region(rr) : null;
            default:
                var m = monitors.FirstOrDefault(x => x.DeviceName == Settings.MonitorDevice) ?? monitors[0];
                return CaptureTarget.FullScreen(m);
        }
    }

    public async Task StartNowAsync()
    {
        if (IsBusy) return;
        // Mark what is being recorded (e.g. when started by hotkey). The frame never appears in the recording.
        if (Settings.Area == AreaKind.Region && RegionFrame == null) ShowRegionFrame();
        var target = BuildTarget();
        if (target == null)
        {
            Dialogs.Warn(Settings.Area == AreaKind.Window
                ? "Choose the window to record first: pick \"Window\" in Area and click the window."
                : "Choose the region to record first.", "Nothing to record");
            return;
        }
        if (!Core.Encoding.FFmpeg.IsAvailable)
        {
            Dialogs.Warn("FFmpeg was not found. Put ffmpeg.exe next to JarvoScreenRecorder.exe, or install it with:\n\nwinget install Gyan.FFmpeg", "FFmpeg needed");
            return;
        }
        if (target is CaptureTarget.Area a) Settings.LastRegion = Settings.Area == AreaKind.Region ? a.Bounds : Settings.LastRegion;
        Settings.Save();

        SetState(RecorderState.Starting);
        string dir = NewProjectDir();
        try
        {
            if (Settings.HideDesktopIcons) _iconsHidden = DesktopIcons.Hide();
            if (Settings.Area == AreaKind.Window && RegionFrame == null && PickedWindow != null) ShowWindowFrame(PickedWindow.Handle);
            var options = new RecordingOptions
            {
                Target = target,
                Fps = Settings.Fps,
                RecordMicrophone = Settings.RecordMicrophone,
                MicrophoneDeviceId = Settings.MicrophoneDeviceId,
                RecordPcSounds = Settings.RecordPcSounds,
                PcSoundsDeviceId = Settings.PcSoundsDeviceId,
                RecordWebcam = Settings.RecordWebcam,
                WebcamDeviceId = Settings.WebcamDeviceId,
                CaptureKeystrokes = Settings.CaptureKeystrokes,
                MaskPasswords = Settings.MaskPasswords,
                SizeLimitBytes = Settings.SizeLimit == SizeLimitMode.StopAtSize ? Settings.SizeLimitMb * 1024L * 1024L : null,
            };
            _session = await RecordingSession.StartAsync(options, dir);
            var dispatcher = Application.Current.Dispatcher;
            _session.SizeLimitReached += () => dispatcher.BeginInvoke(() => _ = StopAsync("The recording reached its size limit and was stopped."));
            _session.DiskAlmostFull += () => dispatcher.BeginInvoke(() => _ = StopAsync("The disk is almost full, so recording was stopped."));
            _session.Failed += msg => dispatcher.BeginInvoke(() => _ = StopAsync(msg));
            RegionFrame?.SetRecording(true);
            SetState(RecorderState.Recording);
        }
        catch (Exception ex)
        {
            RestoreDesktop();
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            _session = null;
            SetState(RecorderState.Idle);
            Dialogs.Error($"Recording could not start.\n\n{ex.Message}");
        }
    }

    private string NewProjectDir()
    {
        Directory.CreateDirectory(Settings.OutputFolder);
        string name = $"Recording {DateTime.Now:yyyy-MM-dd HH.mm.ss}";
        string dir = Path.Combine(Settings.OutputFolder, name);
        for (int i = 2; Directory.Exists(dir); i++) dir = Path.Combine(Settings.OutputFolder, $"{name} ({i})");
        return dir;
    }

    // ---------------- while recording ----------------

    public void TogglePause()
    {
        if (_session == null) return;
        if (State == RecorderState.Recording) { _session.Pause(); SetState(RecorderState.Paused); }
        else if (State == RecorderState.Paused) { _session.Resume(); SetState(RecorderState.Recording); }
    }

    /// <summary>Pauses, asks for a marker name, then carries on (as in FlashBack).</summary>
    public void AddMarker()
    {
        if (_session == null || State is not (RecorderState.Recording or RecorderState.Paused)) return;
        bool wasRecording = State == RecorderState.Recording;
        double at = _session.Elapsed.TotalSeconds;
        if (wasRecording) TogglePause();
        string? name = InputDialog.Ask("Add marker", "Marker name:", $"Marker {(_session.Manifest.Markers.Count + 1)}");
        if (name != null) _session.AddMarker(name, at);
        if (wasRecording && State == RecorderState.Paused) TogglePause();
    }

    /// <summary>Discards everything recorded so far and starts again with the same settings.</summary>
    public async Task RestartAsync()
    {
        if (_session == null) return;
        string dir = _session.ProjectDir;
        await StopCoreAsync();
        try { Directory.Delete(dir, true); } catch { }
        await StartNowAsync();
    }

    public async Task StopAsync(string? reason = null)
    {
        if (_session == null || State == RecorderState.Stopping) return;
        var result = await StopCoreAsync();
        if (result == null) return;
        Settings.AddRecent(result.Value.Dir);
        var w = new RecordingFinishedWindow(this, result.Value.Dir, result.Value.Manifest, reason);
        w.Show();
        w.Activate();
    }

    private async Task<(string Dir, Core.Project.ProjectManifest Manifest)?> StopCoreAsync()
    {
        var session = _session;
        if (session == null) return null;
        SetState(RecorderState.Stopping);
        CloseNote(save: true);
        try
        {
            var manifest = await session.StopAsync();
            return (session.ProjectDir, manifest);
        }
        catch (Exception ex)
        {
            Dialogs.Error($"The recording could not be finished cleanly. What was recorded is in:\n{session.ProjectDir}\n\n{ex.Message}");
            return null;
        }
        finally
        {
            await session.DisposeAsync();
            _session = null;
            _scheduledStop = null;
            RestoreDesktop();
            // The red frame goes away when recording stops; it returns when you next record a region.
            CloseRegionFrame();
            SetState(RecorderState.Idle);
        }
    }

    private void RestoreDesktop()
    {
        if (_iconsHidden) DesktopIcons.Show();
        _iconsHidden = false;
    }

    /// <summary>Called on exit: never leave the desktop icons hidden.</summary>
    public async Task ShutdownAsync()
    {
        if (_session != null) await StopCoreAsync();
        RestoreDesktop();
    }

    // ---------------- timer: schedule, notes ----------------

    private void OnTick()
    {
        if (State == RecorderState.Scheduled && DateTime.Now >= Settings.ScheduleStart) _ = StartScheduledAsync();
        if (_scheduledStop is { } stopAt && DateTime.Now >= stopAt && State is RecorderState.Recording or RecorderState.Paused)
        {
            _scheduledStop = null;
            _ = StopAsync("The scheduled recording finished.");
        }
        if (Settings.NotesEnabled && State == RecorderState.Recording && _note == null) WatchNotesEdge();
        // Other always-on-top windows (meeting apps, full-screen players) can cover the red frame; bring it back.
        if (++_ticks % 10 == 0) RegionFrame?.KeepOnTop();
        Tick?.Invoke();
    }

    private void WatchNotesEdge()
    {
        if (!GetCursorPos(out var p)) return;
        var screen = Displays.GetMonitors().FirstOrDefault(m => p.X >= m.Bounds.X && p.X < m.Bounds.Right && p.Y >= m.Bounds.Y && p.Y < m.Bounds.Bottom);
        if (screen == null) return;
        var b = screen.Bounds;
        bool atEdge = Settings.NotesEdge switch
        {
            ScreenEdge.Top => p.Y <= b.Y,
            ScreenEdge.Bottom => p.Y >= b.Bottom - 1,
            ScreenEdge.Left => p.X <= b.X,
            _ => p.X >= b.Right - 1,
        };
        if (!atEdge) { _cursorAtEdgeSince = DateTime.MaxValue; return; }
        if (_cursorAtEdgeSince == DateTime.MaxValue) { _cursorAtEdgeSince = DateTime.Now; return; }
        if ((DateTime.Now - _cursorAtEdgeSince).TotalMilliseconds < 300) return;
        _cursorAtEdgeSince = DateTime.MaxValue;
        OpenNote(b, p);
    }

    private void OpenNote(PixelRect screen, Native.POINT cursor)
    {
        if (_session == null) return;
        double at = _session.Elapsed.TotalSeconds;
        if (Settings.NotesPauseWhileOpen && State == RecorderState.Recording) TogglePause();
        _note = new NoteWindow(at, Settings.NotesSaveOnLeave);
        _note.Closed += (_, _) =>
        {
            var n = _note;
            _note = null;
            if (n?.SavedText is { Length: > 0 } text) _session?.AddNote(text, n.TimeSeconds);
            if (Settings.NotesPauseWhileOpen && State == RecorderState.Paused) TogglePause();
        };
        _note.ShowNear(screen, cursor.X, cursor.Y, Settings.NotesEdge);
    }

    private void CloseNote(bool save)
    {
        if (_note == null) return;
        if (save) _note.SaveAndClose(); else _note.Close();
    }
}
