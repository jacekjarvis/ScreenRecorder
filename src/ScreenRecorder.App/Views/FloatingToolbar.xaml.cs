using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ScreenRecorder.Core.Interop;

namespace ScreenRecorder.App.Views;

/// <summary>Minimized "floating controls": logo/drag handle, Record, Stop, Pause and the main menu.</summary>
public partial class FloatingToolbar : Window
{
    private readonly RecorderController _c;

    public FloatingToolbar(RecorderController controller)
    {
        InitializeComponent();
        _c = controller;
        _c.StateChanged += Update;
        _c.Tick += UpdateTime;
        Closed += (_, _) => { _c.StateChanged -= Update; _c.Tick -= UpdateTime; };
        SourceInitialized += (_, _) => Displays.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) =>
        {
            Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - ActualWidth) / 2;
            Top = SystemParameters.WorkArea.Bottom - ActualHeight - 12;
            Update();
        };
    }

    private void Update()
    {
        bool recording = _c.State is RecorderState.Recording or RecorderState.Paused;
        RecordButton.IsEnabled = !_c.IsBusy;
        StopButton.IsEnabled = recording;
        PauseButton.IsEnabled = recording;
        PauseGlyph.Text = _c.State == RecorderState.Paused ? "" : "";
        UpdateTime();
    }

    private void UpdateTime() =>
        TimeText.Text = _c.State is RecorderState.Recording or RecorderState.Paused ? _c.Elapsed.ToString(@"hh\:mm\:ss") : "";

    private void Handle_Down(object sender, MouseButtonEventArgs e) => DragMove();
    private async void Record_Click(object sender, RoutedEventArgs e) => await _c.RecordAsync();
    private async void Stop_Click(object sender, RoutedEventArgs e) => await _c.StopAsync();
    private void Pause_Click(object sender, RoutedEventArgs e) => _c.TogglePause();
    private void Menu_Click(object sender, RoutedEventArgs e) => App.Current.ShowRecorderMenu();
}
