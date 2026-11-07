using System.IO;
using System.Windows;
using System.Windows.Threading;
using ScreenRecorder.App.Services;
using ScreenRecorder.App.Views;
using ScreenRecorder.Core.Desktop;

namespace ScreenRecorder.App;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;
    public const string ProductName = "Jarvo Screen Recorder";

    private Mutex? _singleInstance;
    private AppSettings _settings = null!;
    private RecorderController _controller = null!;
    private Hotkeys? _hotkeys;
    private TrayIcon? _tray;
    private RecorderWindow? _recorder;
    private FloatingToolbar? _floating;
    private PlayerWindow? _player;
    private bool _exiting;

    public RecorderController Controller => _controller;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) => LogError(args.ExceptionObject as Exception);

        // Rounded corners (Windows 11) for every window as it opens.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((s, _) => WindowCorners.Apply((Window)s)));

        bool devMode = e.Args.Length >= 2 && e.Args[0] is "--selftest" or "--snapshot";
        bool first = true;
        if (!devMode) _singleInstance = new Mutex(true, "ScreenRecorder.SingleInstance", out first);
        if (!first)
        {
            // Already running: pass on the request (open a recording, or just show the Recorder).
            if (devMode || !InstanceChannel.Send(e.Args.Length > 0 ? e.Args[0] : InstanceChannel.ShowMessage))
                Dialogs.Info($"{ProductName} is already running — look for its icon in the notification area.");
            Shutdown();
            return;
        }

        _settings = AppSettings.Load();
        _controller = new RecorderController(_settings);
        if (e.Args.Length >= 2 && e.Args[0] == "--selftest")
        {
            Dispatcher.BeginInvoke(async () =>
            {
                await Snapshots.SelfTestAsync(e.Args[1], _controller);
                Shutdown();
            });
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--snapshot")
        {
            Dispatcher.BeginInvoke(async () =>
            {
                try { await Snapshots.RunAsync(e.Args[1], e.Args.Length > 2 ? e.Args[2] : null, _controller); }
                catch (Exception ex) { LogError(ex); }
                Shutdown();
            });
            return;
        }
        _tray = new TrayIcon(_controller);
        _hotkeys = new Hotkeys();
        RegisterHotkeys();
        _ = Task.Run(() => { try { _ = Core.Encoding.FFmpeg.BestEncoder; } catch { } }); // warm up encoder detection
        _ = Task.Run(FileAssociation.Register);
        InstanceChannel.Listen(message => Dispatcher.BeginInvoke(() => OpenFromOutside(message)));

        // Double-clicking a .jsrec (or "JarvoScreenRecorder.exe <recording>") opens straight into the Video Editor.
        if (e.Args.Length > 0 && Core.Project.ProjectManifest.ResolveDirectory(e.Args[0]) is { } dir)
        {
            OpenPlayer(dir);
            return;
        }
        _recorder = new RecorderWindow(_controller);
        _recorder.Show();
    }

    private void OpenFromOutside(string message)
    {
        if (Core.Project.ProjectManifest.ResolveDirectory(message) is { } dir) OpenPlayer(dir);
        else RestoreRecorder();
    }

    // ---------------- recorder window / minimizing ----------------

    public void MinimizeRecorder()
    {
        if (_recorder == null) return;
        _recorder.Hide();
        ApplyMinimizeMode();
    }

    public void RestoreRecorder()
    {
        _recorder ??= new RecorderWindow(_controller);
        _floating?.Close();
        _floating = null;
        _recorder.Show();
        _recorder.WindowState = WindowState.Normal;
        _recorder.Activate();
    }

    /// <summary>Floating controls appear only while the Recorder is minimized in that mode.</summary>
    public void ApplyMinimizeMode()
    {
        bool minimized = _recorder == null || !_recorder.IsVisible;
        if (minimized && _settings.MinimizeTo == MinimizeMode.FloatingControls)
        {
            if (_floating == null)
            {
                _floating = new FloatingToolbar(_controller);
                _floating.Closed += (_, _) => _floating = null;
                _floating.Show();
            }
        }
        else
        {
            _floating?.Close();
            _floating = null;
        }
    }

    public void ShowRecorderMenu() => _tray?.ShowMenu();

    // ---------------- other windows ----------------

    public void OpenPlayer(string? projectDir)
    {
        if (_player == null)
        {
            _player = new PlayerWindow(_settings);
            _player.Closed += (_, _) => _player = null;
        }
        _player.Show();
        if (_player.WindowState == WindowState.Minimized) _player.WindowState = WindowState.Normal;
        _player.Activate();
        if (projectDir != null && Core.Project.ProjectManifest.Exists(projectDir))
            _player.Open(projectDir);
    }

    public void OpenSettings(Window? owner)
    {
        var w = new SettingsWindow(_settings);
        if (owner is { IsVisible: true }) w.Owner = owner; else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (w.ShowDialog() == true)
        {
            RegisterHotkeys();
            ApplyMinimizeMode();
            _recorder?.Reload();
        }
    }

    public void ShowHelp() => Dialogs.Info(
        "1. Choose the Area: full screen, a window (click it), or a region (move and resize the red frame).\n" +
        "2. Tick Microphone, PC Sounds and Webcam as needed.\n" +
        "3. Press Record. Press Stop (or the Start/Stop hotkey) when done.\n" +
        "4. Open the recording in the Video Editor to trim it and export an MP4.\n\n" +
        $"Hotkeys:\n  Start / stop: {_settings.HotkeyStartStop}\n  Pause / resume: {_settings.HotkeyPause}\n  Recording controls: {_settings.HotkeyControls}\n  Add marker: {_settings.HotkeyMarker}",
        "Help");

    public void ShowAbout() => Dialogs.Info(
        $"{ProductName} {typeof(App).Assembly.GetName().Version?.ToString(3)}\n\nRecordings: {_settings.OutputFolder}\nVideo encoder: {(Core.Encoding.FFmpeg.IsAvailable ? Core.Encoding.FFmpeg.BestEncoder.ToString() : "FFmpeg not found")}",
        $"About {ProductName}");

    // ---------------- hotkeys ----------------

    private void RegisterHotkeys()
    {
        if (_hotkeys == null) return;
        _hotkeys.UnregisterAll();
        var failed = new List<string>();
        void Reg(string text, string name, Action action)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            if (!_hotkeys.Register(text, action)) failed.Add($"{name} ({text})");
        }
        Reg(_settings.HotkeyStartStop, "Start / stop", () =>
        {
            if (_controller.State is RecorderState.Recording or RecorderState.Paused) _ = _controller.StopAsync();
            else _ = StartFromHotkeyAsync();
        });
        Reg(_settings.HotkeyPause, "Pause / resume", _controller.TogglePause);
        Reg(_settings.HotkeyControls, "Recording controls", () =>
        {
            if (_controller.State is RecorderState.Recording or RecorderState.Paused)
            {
                var w = new RecordingControlsWindow(_controller);
                w.Show();
                w.Activate();
            }
            else RestoreRecorder();
        });
        Reg(_settings.HotkeyMarker, "Add marker", _controller.AddMarker);
        if (failed.Count > 0)
            Dialogs.Warn($"These hotkeys are already used by another program, so they won't work:\n\n{string.Join("\n", failed)}\n\nChange them in Settings → Hotkeys.");
    }

    private async Task StartFromHotkeyAsync()
    {
        await _controller.RecordAsync();
        if (_controller.State == RecorderState.Recording && _settings.MinimizeWhileRecording && _recorder?.IsVisible == true) MinimizeRecorder();
    }

    // ---------------- exit / errors ----------------

    public async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        await _controller.ShutdownAsync();
        _settings.Save();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError(e.Exception);
        e.Handled = true;
        Dialogs.Error($"Something went wrong:\n\n{e.Exception.Message}\n\nDetails were saved to {LogPath}.");
    }

    private static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenRecorder", "error.log");

    private static void LogError(Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"[{DateTime.Now:u}] {ex}\n\n");
        }
        catch { }
        try { DesktopIcons.Show(); } catch { }
    }
}
