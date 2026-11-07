using System.Diagnostics;
using System.IO;
using System.Windows;
using ScreenRecorder.Core.Encoding;
using ScreenRecorder.Core.Export;
using ScreenRecorder.Core.Project;

namespace ScreenRecorder.App.Views;

/// <summary>Export the recording (or the trimmed part) to MP4. Quality defaults to the highest.</summary>
public partial class ExportWindow : Window
{
    private sealed record Option<T>(T Value, string Text)
    {
        public override string ToString() => Text;
    }

    private readonly AppSettings _s;
    private readonly string _dir;
    private readonly long _first;
    private readonly long _last;
    private readonly ProjectManifest _m;
    private CancellationTokenSource? _cts;
    private bool _done;

    public ExportWindow(AppSettings settings, string projectDir, long firstFrame, long? lastFrame)
    {
        InitializeComponent();
        _s = settings;
        _dir = projectDir;
        _m = ProjectManifest.Load(projectDir);
        long total = Mp4Exporter.FrameCount(projectDir, _m);
        _first = Math.Clamp(firstFrame, 0, Math.Max(0, total - 1));
        _last = Math.Clamp(lastFrame ?? total - 1, _first, Math.Max(0, total - 1));
        long count = _last - _first + 1;
        bool trimmed = _first > 0 || _last < total - 1;
        RangeText.Text = trimmed
            ? $"Exporting frames {_first:N0}–{_last:N0} of the recording ({TimeSpan.FromSeconds(count / _m.Fps):hh\\:mm\\:ss})"
            : $"Exporting the whole recording ({TimeSpan.FromSeconds(count / _m.Fps):hh\\:mm\\:ss}, {_m.Width}×{_m.Height})";

        var videoOptions = new[]
        {
            new Option<VideoQuality>(Core.Encoding.VideoQuality.Highest, "Highest (largest file)"),
            new Option<VideoQuality>(Core.Encoding.VideoQuality.High, "High"),
            new Option<VideoQuality>(Core.Encoding.VideoQuality.Medium, "Medium"),
            new Option<VideoQuality>(Core.Encoding.VideoQuality.Small, "Small file"),
        };
        VideoQualityCombo.ItemsSource = videoOptions;
        VideoQualityCombo.SelectedItem = videoOptions.First(o => o.Value == settings.ExportVideoQuality);
        var audioOptions = new[]
        {
            new Option<AudioQuality>(Core.Encoding.AudioQuality.Highest, "Highest (AAC 320 kbps)"),
            new Option<AudioQuality>(Core.Encoding.AudioQuality.High, "High (AAC 256 kbps)"),
            new Option<AudioQuality>(Core.Encoding.AudioQuality.Medium, "Medium (AAC 192 kbps)"),
            new Option<AudioQuality>(Core.Encoding.AudioQuality.Low, "Low (AAC 128 kbps)"),
        };
        AudioQualityCombo.ItemsSource = audioOptions;
        AudioQualityCombo.SelectedItem = audioOptions.First(o => o.Value == settings.ExportAudioQuality);

        IncCursor.IsChecked = true;
        IncPc.IsChecked = IncPc.IsEnabled = _m.SystemAudio != null;
        IncMic.IsChecked = IncMic.IsEnabled = _m.Microphone != null;
        IncCam.IsChecked = IncCam.IsEnabled = _m.Webcam != null;
        UseHardware.IsChecked = settings.ExportUseHardware;
        if (FFmpeg.IsAvailable && FFmpeg.BestEncoder == VideoEncoderKind.Software)
        {
            UseHardware.IsChecked = false;
            UseHardware.IsEnabled = false;
            UseHardware.Content = "Use the graphics card to encode (not available on this PC)";
        }

        string folder = settings.LastExportFolder is { } f && Directory.Exists(f)
            ? f : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        string name = Path.GetFileName(projectDir) + (trimmed ? $" (frames {_first}-{_last})" : "");
        OutputPath.Text = Path.Combine(folder, name + ".mp4");
        Closing += (_, e) =>
        {
            if (_cts != null && !_done)
            {
                if (!Dialogs.Confirm("Stop exporting?", "Export to MP4", "Stop", "Keep going")) { e.Cancel = true; return; }
                _cts.Cancel();
            }
        };
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export to MP4",
            Filter = "MP4 video|*.mp4",
            FileName = Path.GetFileName(OutputPath.Text),
            InitialDirectory = Path.GetDirectoryName(OutputPath.Text),
        };
        if (dlg.ShowDialog(this) == true) OutputPath.Text = dlg.FileName;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        string output = OutputPath.Text.Trim();
        if (output.Length == 0) return;
        if (!output.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) output += ".mp4";
        if (File.Exists(output) && !Dialogs.Confirm($"{Path.GetFileName(output)} already exists. Replace it?", "Export to MP4", "Replace", "Cancel")) return;

        var vq = ((Option<VideoQuality>)VideoQualityCombo.SelectedItem).Value;
        var aq = ((Option<AudioQuality>)AudioQualityCombo.SelectedItem).Value;
        _s.ExportVideoQuality = vq;
        _s.ExportAudioQuality = aq;
        _s.ExportUseHardware = UseHardware.IsChecked == true;
        _s.LastExportFolder = Path.GetDirectoryName(output);
        _s.Save();

        var options = new ExportOptions
        {
            StartFrame = _first,
            EndFrame = _last,
            VideoQuality = vq,
            AudioQuality = aq,
            IncludeCursor = IncCursor.IsChecked == true,
            IncludePcSounds = IncPc.IsChecked == true,
            IncludeMicrophone = IncMic.IsChecked == true,
            IncludeWebcam = IncCam.IsChecked == true,
            UseHardwareEncoder = UseHardware.IsChecked == true,
        };

        OptionsPanel.IsEnabled = false;
        ExportButton.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressText.Text = "Starting…";
        _cts = new CancellationTokenSource();
        var progress = new Progress<ExportProgress>(p =>
        {
            Progress.Value = p.Fraction;
            string left = p.Remaining is { } r ? $"  ·  about {FormatDuration(r)} left" : "";
            ProgressText.Text = $"{p.Fraction:P0}  ·  frame {p.FramesDone:N0} of {p.FramesTotal:N0}{left}";
            Bar.Title = $"Export to MP4 — {p.Fraction:P0}";
        });
        var started = DateTime.Now;
        try
        {
            await Mp4Exporter.ExportAsync(_dir, output, options, progress, _cts.Token);
            _done = true;
            var size = new FileInfo(output).Length;
            Progress.Value = 1;
            ProgressText.Text = $"Done in {FormatDuration(DateTime.Now - started)}  ·  {Path.GetFileName(output)}  ·  {size / (1024.0 * 1024):0.#} MB";
            Bar.Title = "Export to MP4 — done";
            CancelButton.Content = "Close";
            OpenFileButton.Visibility = ShowFolderButton.Visibility = Visibility.Visible;
            OpenFileButton.Tag = output;
        }
        catch (OperationCanceledException)
        {
            _done = true;
            ProgressText.Text = "Export cancelled.";
            CancelButton.Content = "Close";
        }
        catch (Exception ex)
        {
            _done = true;
            ProgressText.Text = "Export failed.";
            CancelButton.Content = "Close";
            Dialogs.Error($"The export failed.\n\n{ex.Message}");
        }
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes} min" :
        t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds} s" : $"{Math.Max(1, (int)t.TotalSeconds)} s";

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (OpenFileButton.Tag is string path) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private void ShowFolder_Click(object sender, RoutedEventArgs e)
    {
        if (OpenFileButton.Tag is string path) Process.Start("explorer.exe", $"/select,\"{path}\"");
    }
}
