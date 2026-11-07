using System.Diagnostics;
using System.IO;
using System.Windows;
using ScreenRecorder.Core.Project;

namespace ScreenRecorder.App.Views;

/// <summary>Shown after Stop: open the recording, export it, or discard it.</summary>
public partial class RecordingFinishedWindow : Window
{
    private readonly RecorderController _c;
    private readonly string _dir;

    public RecordingFinishedWindow(RecorderController controller, string dir, ProjectManifest m, string? reason)
    {
        InitializeComponent();
        _c = controller;
        _dir = dir;
        if (reason != null) { Reason.Text = reason; Reason.Visibility = Visibility.Visible; }
        long bytes = 0;
        try { bytes = new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); } catch { }
        Summary.Text = $"{TimeSpan.FromSeconds(m.DurationSeconds):hh\\:mm\\:ss} recorded  ·  {bytes / (1024.0 * 1024):0.#} MB";
        var tracks = new List<string> { $"{m.Width}×{m.Height} at {m.Fps:0} fps" };
        if (m.SystemAudio != null) tracks.Add("PC sounds");
        if (m.Microphone != null) tracks.Add("microphone");
        if (m.Webcam != null) tracks.Add("webcam");
        if (m.Markers.Count > 0) tracks.Add($"{m.Markers.Count} marker(s)");
        if (m.Notes.Count > 0) tracks.Add($"{m.Notes.Count} note(s)");
        string file = Path.GetFileName(ProjectManifest.FindFile(dir) ?? dir);
        Details.Text = string.Join(" · ", tracks) +
                       $"\nSaved as \"{file}\" in {dir}" +
                       "\nIt's already saved — close this and double-click that file any time to trim and export it later.";
        if (m.Warnings.Count > 0)
        {
            Warnings.Text = string.Join("\n", m.Warnings);
            Warnings.Visibility = Visibility.Visible;
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e) { App.Current.OpenPlayer(_dir); Close(); }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        Close();
        var w = new ExportWindow(_c.Settings, _dir);
        w.Show();
    }

    private void Folder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(_dir) { UseShellExecute = true });

    private void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.Confirm("Delete this recording? This can't be undone.", "Discard recording", "Delete", "Keep")) return;
        try
        {
            Directory.Delete(_dir, true);
            _c.Settings.RecentRecordings.Remove(_dir);
            _c.Settings.Save();
            Close();
        }
        catch (Exception ex) { Dialogs.Error($"The recording could not be deleted: {ex.Message}"); }
    }
}
