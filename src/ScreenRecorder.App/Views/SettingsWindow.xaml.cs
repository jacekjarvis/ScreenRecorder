using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ScreenRecorder.App.Services;
using ScreenRecorder.Core.Audio;
using ScreenRecorder.Core.Webcam;

namespace ScreenRecorder.App.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _s;
    private sealed record Choice(string? Id, string Name)
    {
        public override string ToString() => Name;
    }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _s = settings;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        MinTray.IsChecked = _s.MinimizeTo == MinimizeMode.Tray;
        MinFloating.IsChecked = _s.MinimizeTo == MinimizeMode.FloatingControls;
        MinWhileRecording.IsChecked = _s.MinimizeWhileRecording;

        FpsCombo.ItemsSource = new[] { 10, 15, 20, 25, 30, 60 };
        FpsCombo.SelectedItem = _s.Fps;
        EncoderText.Text = Core.Encoding.FFmpeg.IsAvailable
            ? $"Video encoder: {Core.Encoding.FFmpeg.BestEncoder switch { Core.Encoding.VideoEncoderKind.Nvidia => "NVIDIA graphics", Core.Encoding.VideoEncoderKind.Intel => "Intel graphics", Core.Encoding.VideoEncoderKind.Amd => "AMD graphics", _ => "software (no graphics encoder found)" }}"
            : "FFmpeg was not found — recording needs ffmpeg.exe.";

        Fill(MicCombo, AudioDevices.GetInputs().Select(d => new Choice(d.Id, d.Name + (d.IsDefault ? "  (default)" : ""))), _s.MicrophoneDeviceId);
        Fill(PcCombo, AudioDevices.GetOutputs().Select(d => new Choice(d.Id, d.Name + (d.IsDefault ? "  (default)" : ""))), _s.PcSoundsDeviceId);
        try { Fill(CamCombo, (await WebcamCapture.ListAsync()).Select(d => new Choice(d.Id, d.Name)), _s.WebcamDeviceId); }
        catch { Fill(CamCombo, Array.Empty<Choice>(), null); }

        HideIcons.IsChecked = _s.HideDesktopIcons;
        CaptureKeys.IsChecked = _s.CaptureKeystrokes;
        MaskPasswords.IsChecked = _s.MaskPasswords;

        HkStartStop.Text = _s.HotkeyStartStop;
        HkPause.Text = _s.HotkeyPause;
        HkControls.Text = _s.HotkeyControls;
        HkMarker.Text = _s.HotkeyMarker;

        SizeUnlimited.IsChecked = _s.SizeLimit == SizeLimitMode.Unlimited;
        SizeLimited.IsChecked = _s.SizeLimit == SizeLimitMode.StopAtSize;
        SizeMb.Text = _s.SizeLimitMb.ToString();

        NotesEnabled.IsChecked = _s.NotesEnabled;
        NotesEdge.ItemsSource = Enum.GetValues<ScreenEdge>();
        NotesEdge.SelectedItem = _s.NotesEdge;
        NotesPause.IsChecked = _s.NotesPauseWhileOpen;
        NotesAutoSave.IsChecked = _s.NotesSaveOnLeave;

        OutputFolder.Text = _s.OutputFolder;
    }

    private static void Fill(ComboBox combo, IEnumerable<Choice> devices, string? selectedId)
    {
        var list = new List<Choice> { new(null, "Default device") };
        list.AddRange(devices);
        combo.ItemsSource = list;
        combo.SelectedItem = list.FirstOrDefault(c => c.Id == selectedId) ?? list[0];
    }

    private void Hotkey_KeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Back or Key.Delete) { ((TextBox)sender).Text = ""; return; }
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.None && key is not (>= Key.F1 and <= Key.F24)) return; // plain letters would block typing
        ((TextBox)sender).Text = Hotkeys.Format(mods, key);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { InitialDirectory = OutputFolder.Text, Title = "Folder for recordings" };
        if (dlg.ShowDialog(this) == true) OutputFolder.Text = dlg.FolderName;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (SizeLimited.IsChecked == true && (!int.TryParse(SizeMb.Text, out int mb) || mb < 10))
        {
            Tabs.SelectedIndex = 7;
            Dialogs.Warn("Enter a size limit of at least 10 MB.");
            return;
        }
        _s.MinimizeTo = MinFloating.IsChecked == true ? MinimizeMode.FloatingControls : MinimizeMode.Tray;
        _s.MinimizeWhileRecording = MinWhileRecording.IsChecked == true;
        _s.Fps = FpsCombo.SelectedItem is int fps ? fps : 30;
        _s.MicrophoneDeviceId = (MicCombo.SelectedItem as Choice)?.Id;
        _s.PcSoundsDeviceId = (PcCombo.SelectedItem as Choice)?.Id;
        _s.WebcamDeviceId = (CamCombo.SelectedItem as Choice)?.Id;
        _s.HideDesktopIcons = HideIcons.IsChecked == true;
        _s.CaptureKeystrokes = CaptureKeys.IsChecked == true;
        _s.MaskPasswords = MaskPasswords.IsChecked == true;
        _s.HotkeyStartStop = HkStartStop.Text;
        _s.HotkeyPause = HkPause.Text;
        _s.HotkeyControls = HkControls.Text;
        _s.HotkeyMarker = HkMarker.Text;
        _s.SizeLimit = SizeLimited.IsChecked == true ? SizeLimitMode.StopAtSize : SizeLimitMode.Unlimited;
        if (int.TryParse(SizeMb.Text, out int limit)) _s.SizeLimitMb = limit;
        _s.NotesEnabled = NotesEnabled.IsChecked == true;
        _s.NotesEdge = NotesEdge.SelectedItem is ScreenEdge edge ? edge : ScreenEdge.Top;
        _s.NotesPauseWhileOpen = NotesPause.IsChecked == true;
        _s.NotesSaveOnLeave = NotesAutoSave.IsChecked == true;
        if (!string.IsNullOrWhiteSpace(OutputFolder.Text)) _s.OutputFolder = OutputFolder.Text.Trim();
        _s.Save();
        DialogResult = true;
    }
}
