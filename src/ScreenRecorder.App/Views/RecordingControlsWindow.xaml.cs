using System.Windows;
using System.Windows.Interop;
using ScreenRecorder.Core.Interop;

namespace ScreenRecorder.App.Views;

/// <summary>Pops up on the hotkey during recording: Stop, Pause/Resume, Restart, Add marker.</summary>
public partial class RecordingControlsWindow : Window
{
    private readonly RecorderController _c;

    public RecordingControlsWindow(RecorderController controller)
    {
        InitializeComponent();
        _c = controller;
        _c.StateChanged += Update;
        _c.Tick += Update;
        Closed += (_, _) => { _c.StateChanged -= Update; _c.Tick -= Update; };
        SourceInitialized += (_, _) => Displays.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => Update();
        Deactivated += (_, _) => { if (IsLoaded) Close(); };
    }

    private void Update()
    {
        if (_c.State is not (RecorderState.Recording or RecorderState.Paused)) { if (IsLoaded) Close(); return; }
        TimeText.Text = _c.Elapsed.ToString(@"hh\:mm\:ss");
        bool paused = _c.State == RecorderState.Paused;
        PauseText.Text = paused ? "Resume" : "Pause";
        Dot.Opacity = paused ? 0.35 : 1;
        StateText.Text = paused ? "Paused" : $"Recording · {_c.SizeBytes / (1024.0 * 1024):0} MB";
    }

    private async void Stop_Click(object sender, RoutedEventArgs e) { Close(); await _c.StopAsync(); }
    private void Pause_Click(object sender, RoutedEventArgs e) => _c.TogglePause();

    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        Close();
        if (Dialogs.Confirm("Discard everything recorded so far and start again?", "Restart recording", "Restart", "Cancel"))
            await _c.RestartAsync();
    }

    private void Marker_Click(object sender, RoutedEventArgs e) { Close(); _c.AddMarker(); }
}
