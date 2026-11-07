using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ScreenRecorder.App.Services;
using ScreenRecorder.Core.Interop;

namespace ScreenRecorder.App.Views;

/// <summary>The Recorder main window — same layout as FlashBack's: Area, Microphone, PC Sounds, Webcam, Record.</summary>
public partial class RecorderWindow : Window
{
    private sealed record AreaItem(string Label, AreaKind Kind, string? Device)
    {
        public override string ToString() => Label;
    }

    private readonly RecorderController _c;
    private bool _loading;
    private bool _picking;

    private AppSettings S => _c.Settings;

    public RecorderWindow(RecorderController controller)
    {
        InitializeComponent();
        _c = controller;
        Bar.MinimizeRequested += (_, _) => App.Current.MinimizeRecorder();
        _c.StateChanged += UpdateState;
        _c.Tick += UpdateTick;
        _c.RegionFrameChanged += UpdateAreaDetail;
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
        _loading = false;
        UpdateAreaDetail();
    }

    private void Options_Changed(object sender, RoutedEventArgs e)
    {
        S.RecordMicrophone = MicCheck.IsChecked == true;
        S.RecordPcSounds = PcCheck.IsChecked == true;
        S.RecordWebcam = CamCheck.IsChecked == true;
        S.Save();
    }

    private async void Area_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AreaCombo.SelectedItem is not AreaItem item) return;
        S.Area = item.Kind;
        S.MonitorDevice = item.Device ?? S.MonitorDevice;
        S.Save();
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
                ? $"Region: {f.RegionPixels.ToEvenSize()}  (move or resize the red frame)"
                : S.LastRegion is { } last ? $"Region: {last.ToEvenSize()}  (press Record to position the red frame)" : "Press Record to position the red frame",
            _ => null,
        };
        AreaDetail.Text = text ?? "";
        AreaDetail.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
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
        MicLevel.Visibility = recording && S.RecordMicrophone ? Visibility.Visible : Visibility.Hidden;
        PcLevel.Visibility = recording && S.RecordPcSounds ? Visibility.Visible : Visibility.Hidden;
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
            MicLevel.Value = s.MicrophoneLevel;
            PcLevel.Value = s.PcSoundsLevel;
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
