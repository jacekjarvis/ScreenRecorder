using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ScreenRecorder.App.Services;
using ScreenRecorder.Core.Audio;
using ScreenRecorder.Core.Interop;

namespace ScreenRecorder.App.Views;

/// <summary>The Recorder main window — same layout as FlashBack's: Area, Microphone, PC Sounds, Webcam, Record.</summary>
public partial class RecorderWindow : Window
{
    private sealed record AreaItem(string Label, AreaKind Kind, string? Device)
    {
        public override string ToString() => Label;
    }

    private sealed record DeviceChoice(string? Id, string Name)
    {
        public override string ToString() => Name;
    }

    private readonly RecorderController _c;
    private readonly DispatcherTimer _meterTimer;
    private AudioLevelMonitor? _micMonitor, _pcMonitor;
    private string? _micMonitorId = "-", _pcMonitorId = "-";
    private bool _loading;
    private bool _picking;
    private ScreenHighlighter? _highlight;

    private AppSettings S => _c.Settings;

    public RecorderWindow(RecorderController controller)
    {
        InitializeComponent();
        _c = controller;
        Bar.MinimizeRequested += (_, _) => App.Current.MinimizeRecorder();
        _c.StateChanged += UpdateState;
        _c.Tick += UpdateTick;
        _c.RegionFrameChanged += UpdateAreaDetail;
        _c.StateChanged += UpdateMonitors;
        _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => UpdateMeters(), Dispatcher);
        IsVisibleChanged += (_, _) => UpdateMonitors();
        SetUpAreaHighlight();
        Closed += (_, _) => { _meterTimer.Stop(); StopMonitor(ref _micMonitor); StopMonitor(ref _pcMonitor); };
        Loaded += (_, _) => { LoadOptions(); UpdateState(); };
        Closing += async (_, e) =>
        {
            if (!ExitOnClose) { _c.CloseRegionFrame(); return; }
            if (_c.IsBusy && !Dialogs.Confirm("A recording is in progress. Stop it and exit?", "Exit Jarvo Screen Recorder", "Stop and exit", "Cancel"))
            {
                e.Cancel = true;
                return;
            }
            _c.CloseRegionFrame();
            await App.Current.ExitAsync();
        };
    }

    /// <summary>Closing the Recorder exits the app (as in FlashBack); off only for UI snapshots.</summary>
    public bool ExitOnClose { get; set; } = true;

    private void LoadOptions()
    {
        _loading = true;
        var items = new List<AreaItem>();
        var monitors = Displays.GetMonitors();
        if (monitors.Count <= 1) items.Add(new AreaItem("Full screen", AreaKind.FullScreen, monitors.FirstOrDefault()?.DeviceName));
        else
        {
            int n = 1;
            foreach (var m in monitors)
                items.Add(new AreaItem($"Full screen — monitor {n++}{(m.IsPrimary ? " (main)" : "")}", AreaKind.FullScreen, m.DeviceName));
            items.Add(new AreaItem("All monitors", AreaKind.AllMonitors, null));
        }
        items.Add(new AreaItem("Window", AreaKind.Window, null));
        items.Add(new AreaItem("Region", AreaKind.Region, null));
        AreaCombo.ItemsSource = items;
        AreaCombo.SelectedItem = items.FirstOrDefault(i => i.Kind == S.Area && (i.Kind != AreaKind.FullScreen || i.Device == S.MonitorDevice))
                                 ?? items.FirstOrDefault(i => i.Kind == S.Area && S.Area != AreaKind.Window)
                                 ?? items[0];
        MicCheck.IsChecked = S.RecordMicrophone;
        PcCheck.IsChecked = S.RecordPcSounds;
        CamCheck.IsChecked = S.RecordWebcam;
        FillDevices(MicDevice, AudioDevices.GetInputs(), S.MicrophoneDeviceId);
        FillDevices(PcDevice, AudioDevices.GetOutputs(), S.PcSoundsDeviceId);
        _loading = false;
        UpdateMonitors();
        UpdateAreaDetail();
    }

    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        S.RecordMicrophone = MicCheck.IsChecked == true;
        S.RecordPcSounds = PcCheck.IsChecked == true;
        S.RecordWebcam = CamCheck.IsChecked == true;
        S.Save();
        UpdateMonitors();
    }

    // ---------------- sound devices and level meters ----------------

    private static void FillDevices(ComboBox combo, IReadOnlyList<AudioDevice> devices, string? selectedId)
    {
        string? defaultName = devices.FirstOrDefault(d => d.IsDefault)?.Name;
        var items = new List<DeviceChoice> { new(null, defaultName != null ? $"Default — {defaultName}" : "Default device") };
        items.AddRange(devices.Select(d => new DeviceChoice(d.Id, d.Name)));
        combo.ItemsSource = items;
        combo.SelectedItem = items.FirstOrDefault(i => i.Id == selectedId) ?? items[0];
    }

    /// <summary>Re-reads the devices when a list is opened, so a newly plugged-in mic appears.</summary>
    private void Devices_Opening(object? sender, EventArgs e)
    {
        _loading = true;
        if (sender == MicDevice) FillDevices(MicDevice, AudioDevices.GetInputs(), S.MicrophoneDeviceId);
        else FillDevices(PcDevice, AudioDevices.GetOutputs(), S.PcSoundsDeviceId);
        _loading = false;
    }

    private void Device_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (sender == MicDevice) S.MicrophoneDeviceId = (MicDevice.SelectedItem as DeviceChoice)?.Id;
        else S.PcSoundsDeviceId = (PcDevice.SelectedItem as DeviceChoice)?.Id;
        S.Save();
        UpdateMonitors();
    }

    /// <summary>
    /// Listens to the ticked sources while the Recorder is open and not recording (during a recording
    /// the meters show what is actually being recorded instead).
    /// </summary>
    private void UpdateMonitors()
    {
        bool idle = IsVisible && !_c.IsBusy;
        SyncMonitor(ref _micMonitor, ref _micMonitorId, idle && MicCheck.IsChecked == true, S.MicrophoneDeviceId, pcSounds: false, MicMeter);
        SyncMonitor(ref _pcMonitor, ref _pcMonitorId, idle && PcCheck.IsChecked == true, S.PcSoundsDeviceId, pcSounds: true, PcMeter);
        bool anyMeter = IsVisible && (MicCheck.IsChecked == true || PcCheck.IsChecked == true);
        if (anyMeter) _meterTimer.Start(); else _meterTimer.Stop();
        MicMeter.Opacity = MicCheck.IsChecked == true ? 1 : 0.35;
        PcMeter.Opacity = PcCheck.IsChecked == true ? 1 : 0.35;
        if (MicCheck.IsChecked != true) MicMeter.Reset();
        if (PcCheck.IsChecked != true) PcMeter.Reset();
    }

    private void SyncMonitor(ref AudioLevelMonitor? monitor, ref string? currentId, bool wanted, string? deviceId, bool pcSounds, LevelMeter meter)
    {
        if (monitor != null && (!wanted || currentId != deviceId)) StopMonitor(ref monitor);
        if (!wanted || monitor != null) return;
        try
        {
            monitor = new AudioLevelMonitor(deviceId, pcSounds);
            currentId = deviceId;
            meter.ToolTip = pcSounds ? $"{monitor.DeviceName}: play some sound and the bar should move" : $"{monitor.DeviceName}: speak and the bar should move";
        }
        catch (Exception ex)
        {
            monitor = null;
            meter.ToolTip = $"This device can't be opened: {ex.Message}";
        }
    }

    private static void StopMonitor(ref AudioLevelMonitor? monitor)
    {
        monitor?.Dispose();
        monitor = null;
    }

    private void UpdateMeters()
    {
        if (_c.Session is { } s && _c.State is RecorderState.Recording or RecorderState.Paused)
        {
            MicMeter.Push(S.RecordMicrophone && _c.State == RecorderState.Recording ? s.MicrophoneLevel : 0);
            PcMeter.Push(S.RecordPcSounds && _c.State == RecorderState.Recording ? s.PcSoundsLevel : 0);
            return;
        }
        if (MicCheck.IsChecked == true) MicMeter.Push(_micMonitor?.TakePeak() ?? 0);
        if (PcCheck.IsChecked == true) PcMeter.Push(_pcMonitor?.TakePeak() ?? 0);
    }

    // ---------------- Area: show which screen will be recorded ----------------

    private static readonly TimeSpan HighlightTime = TimeSpan.FromSeconds(2);

    private void SetUpAreaHighlight()
    {
        _highlight = new ScreenHighlighter(this);
        // Moving through the open list (mouse or arrow keys) previews each screen, as in FlashBack.
        AreaItem? previewed = null;
        var watch = new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Input, (_, _) =>
        {
            var hovered = AreaCombo.Items.OfType<AreaItem>()
                .FirstOrDefault(i => AreaCombo.ItemContainerGenerator.ContainerFromItem(i) is ComboBoxItem { IsHighlighted: true });
            if (hovered != null && hovered != previewed) Highlight(previewed = hovered, stay: true);
        }, Dispatcher) { IsEnabled = false };
        AreaCombo.DropDownOpened += (_, _) =>
        {
            previewed = AreaCombo.SelectedItem as AreaItem;
            if (previewed != null) Highlight(previewed, stay: true);
            watch.Start();
        };
        AreaCombo.DropDownClosed += (_, _) =>
        {
            watch.Stop();
            if (AreaCombo.SelectedItem is AreaItem item) Highlight(item, stay: false);
        };
        _c.StateChanged += () => { if (_c.IsBusy) _highlight.Hide(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) _highlight.Hide(); };
        Closed += (_, _) => _highlight.Hide();
    }

    private void Highlight(AreaItem item, bool stay)
    {
        if (_c.IsBusy || !IsVisible) return;
        _highlight?.Show(item.Kind, item.Device, stay ? null : HighlightTime);
    }

    private async void Area_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AreaCombo.SelectedItem is not AreaItem item) return;
        S.Area = item.Kind;
        S.MonitorDevice = item.Device ?? S.MonitorDevice;
        S.Save();
        if (!AreaCombo.IsDropDownOpen) Highlight(item, stay: false);
        _c.CloseRegionFrame();
        if (item.Kind == AreaKind.Region) _c.ShowRegionFrame();
        if (item.Kind == AreaKind.Window) await PickWindowAsync();
        UpdateAreaDetail();
    }

    /// <summary>"When prompted, click the window you want to record — a red frame will appear around it."</summary>
    private async Task PickWindowAsync()
    {
        if (_picking) return;
        _picking = true;
        var hint = new PickHintWindow("Click the window you want to record.   Esc to cancel.");
        hint.Show();
        try
        {
            IntPtr hwnd = await WindowPicker.PickAsync();
            var ownHandle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || hwnd == ownHandle || hwnd == Native.GetShellWindow())
            {
                _c.PickedWindow = null;
            }
            else
            {
                string title = Displays.GetWindows().FirstOrDefault(w => w.Handle == hwnd)?.Title ?? "Selected window";
                _c.PickedWindow = new WindowInfo(hwnd, title, 0);
                _c.ShowWindowFrame(hwnd);
            }
        }
        finally
        {
            hint.Close();
            _picking = false;
            Activate();
            UpdateAreaDetail();
        }
    }

    private void UpdateAreaDetail()
    {
        string? text = S.Area switch
        {
            AreaKind.Window => _c.PickedWindow is { } w ? $"Window: {w.Title}" : "No window chosen — pick Window again and click a window",
            AreaKind.Region => _c.RegionFrame is { IsLocked: false } f
                ? $"Region: {Describe(f.RegionPixels)}  (move or resize the red frame)"
                : S.LastRegion is { } last ? $"Region: {Describe(last)}  (press Record to position the red frame)" : "Press Record to position the red frame",
            _ => null,
        };
        AreaDetail.Text = text ?? "";
        AreaDetail.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>"1280 × 720 on monitor 2" (the monitor number matches the Area list).</summary>
    private static string Describe(PixelRect r)
    {
        var e = r.ToEvenSize();
        var monitors = Displays.GetMonitors();
        string size = $"{e.Width} × {e.Height}";
        if (monitors.Count < 2) return size;
        int n = monitors.ToList().FindIndex(m => m.DeviceName == Displays.MonitorFor(r).DeviceName) + 1;
        return $"{size} on monitor {n}";
    }

    // ---------------- recording ----------------

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_c.State is RecorderState.Recording or RecorderState.Paused) { await _c.StopAsync(); return; }
        if (S.Area == AreaKind.Region && _c.State == RecorderState.Idle && _c.RegionFrame is not { IsLocked: false })
        {
            // As in FlashBack: first show the red frame, then Record starts once it's where you want it.
            _c.ShowRegionFrame();
            StatusText.Text = "Position the red frame, then press Record";
            return;
        }
        await _c.RecordAsync();
        if (_c.State is RecorderState.Recording && S.MinimizeWhileRecording) App.Current.MinimizeRecorder();
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => _c.TogglePause();

    private void UpdateState()
    {
        bool recording = _c.State is RecorderState.Recording or RecorderState.Paused;
        RecordText.Text = _c.State switch
        {
            RecorderState.Scheduled => "Cancel",
            RecorderState.Starting => "Starting…",
            RecorderState.Stopping => "Saving…",
            _ when recording => "Stop",
            _ => "Record",
        };
        RecordDot.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        StopSquare.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        RecordButton.IsEnabled = _c.State is not (RecorderState.Starting or RecorderState.Stopping);
        PauseButton.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        PauseGlyph.Text = _c.State == RecorderState.Paused ? "" : "";
        AreaCombo.IsEnabled = MicCheck.IsEnabled = PcCheck.IsEnabled = CamCheck.IsEnabled = !_c.IsBusy;
        MicDevice.IsEnabled = PcDevice.IsEnabled = !_c.IsBusy;
        TimerText.Text = S.ScheduleEnabled || _c.State == RecorderState.Scheduled ? "Set Timer •" : "Set Timer";
        Bar.Title = _c.State switch
        {
            RecorderState.Recording => "Jarvo Screen Recorder — recording",
            RecorderState.Paused => "Jarvo Screen Recorder — paused",
            RecorderState.Scheduled => "Jarvo Screen Recorder — waiting for scheduled start",
            _ => App.ProductName,
        };
        UpdateTick();
    }

    private void UpdateTick()
    {
        if (_c.State == RecorderState.Scheduled)
        {
            var wait = S.ScheduleStart - DateTime.Now;
            StatusText.Text = "Scheduled";
            ElapsedText.Text = wait > TimeSpan.Zero ? $"in {wait:hh\\:mm\\:ss}" : "";
            return;
        }
        if (_c.Session is { } s && _c.State is RecorderState.Recording or RecorderState.Paused)
        {
            ElapsedText.Text = _c.Elapsed.ToString(@"hh\:mm\:ss");
            StatusText.Text = _c.State == RecorderState.Paused ? "Paused" : $"{_c.SizeBytes / (1024.0 * 1024):0} MB";
        }
        else
        {
            ElapsedText.Text = "";
            StatusText.Text = "";
        }
    }

    // ---------------- menu band ----------------

    private void Recordings_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = RecordingsButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(MenuItem("Open recording in Video Editor…", () => App.Current.OpenPlayer(null)));
        menu.Items.Add(MenuItem("Open recordings folder", () =>
        {
            Directory.CreateDirectory(S.OutputFolder);
            Process.Start(new ProcessStartInfo(S.OutputFolder) { UseShellExecute = true });
        }));
        var recent = S.RecentRecordings.Where(Directory.Exists).ToList();
        if (recent.Count > 0)
        {
            menu.Items.Add(new Separator { Margin = new Thickness(0, 4, 0, 4) });
            foreach (var dir in recent)
                menu.Items.Add(MenuItem(Path.GetFileName(dir), () => App.Current.OpenPlayer(dir)));
        }
        menu.IsOpen = true;
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = HelpButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        menu.Items.Add(MenuItem("Help", App.Current.ShowHelp));
        menu.Items.Add(MenuItem("About", App.Current.ShowAbout));
        menu.IsOpen = true;
    }

    private void SetTimer_Click(object sender, RoutedEventArgs e)
    {
        new TimerWindow(S) { Owner = this }.ShowDialog();
        UpdateState();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => App.Current.OpenSettings(this);

    private void VideoEditor_Click(object sender, RoutedEventArgs e) => App.Current.OpenPlayer(S.RecentRecordings.FirstOrDefault(Directory.Exists));

    private static MenuItem MenuItem(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>Called after Settings closes so the main window reflects any changes.</summary>
    public void Reload() => LoadOptions();
}
