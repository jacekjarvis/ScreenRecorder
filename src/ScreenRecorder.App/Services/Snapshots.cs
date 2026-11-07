using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenRecorder.App.Views;

namespace ScreenRecorder.App.Services;

/// <summary>
/// Developer aid: "ScreenRecorder.exe --snapshot &lt;outDir&gt; [recordingDir]" renders each window
/// off-screen to PNG files and exits, so the UI can be reviewed without using the desktop.
/// </summary>
internal static class Snapshots
{
    public static async Task RunAsync(string outDir, string? recording, RecorderController controller)
    {
        Directory.CreateDirectory(outDir);
        var settings = controller.Settings;
        await Render(new RecorderWindow(controller) { ExitOnClose = false }, Path.Combine(outDir, "recorder.png"));
        await Render(new SettingsWindow(settings), Path.Combine(outDir, "settings.png"), 1500);
        await Render(new TimerWindow(settings), Path.Combine(outDir, "timer.png"));
        await Render(new FloatingToolbar(controller), Path.Combine(outDir, "floating.png"));
        if (recording != null)
        {
            var player = new PlayerWindow(settings);
            await Render(player, Path.Combine(outDir, "player.png"), 2500, w =>
            {
                ((PlayerWindow)w).Open(recording);
            });
            await Render(new ExportWindow(settings, recording, 30, 119), Path.Combine(outDir, "export.png"));
            var m = Core.Project.ProjectManifest.Load(recording);
            await Render(new RecordingFinishedWindow(controller, recording, m, null), Path.Combine(outDir, "finished.png"));
        }
    }

    /// <summary>"--selftest &lt;outDir&gt;": records 3 s of the main screen through the same controller the Record button uses.</summary>
    public static async Task SelfTestAsync(string outDir, RecorderController controller)
    {
        Directory.CreateDirectory(outDir);
        string log = Path.Combine(outDir, "selftest.txt");
        void Log(string text) => File.AppendAllText(log, text + Environment.NewLine);
        var s = controller.Settings;
        s.OutputFolder = Path.Combine(outDir, "recordings");
        s.Area = AreaKind.FullScreen;
        s.RecordPcSounds = true;
        s.HideDesktopIcons = false;
        s.ScheduleEnabled = false;
        try
        {
            await controller.StartNowAsync();
            Log($"after start: {controller.State}");
            await Task.Delay(1500);
            controller.TogglePause();
            Log($"after pause: {controller.State}");
            await Task.Delay(1000);
            controller.TogglePause();
            await Task.Delay(1500);
            await controller.StopAsync();
            Log($"after stop: {controller.State}");
            foreach (var dir in Directory.GetDirectories(s.OutputFolder))
            {
                var m = Core.Project.ProjectManifest.Load(dir);
                Log($"project: {Path.GetFileName(dir)} frames={m.Frames} duration={m.DurationSeconds:0.00} completed={m.Completed} warnings={string.Join(" | ", m.Warnings)}");
            }
        }
        catch (Exception ex) { Log($"FAILED: {ex}"); }
        foreach (var w in Application.Current.Windows.Cast<Window>().ToList()) w.Close();
    }

    private static async Task Render(Window w, string path, int waitMs = 400, Action<Window>? afterShow = null)
    {
        try { await RenderCore(w, path, waitMs, afterShow); }
        catch (Exception ex)
        {
            File.AppendAllText(Path.Combine(Path.GetDirectoryName(path)!, "errors.txt"), $"{Path.GetFileName(path)}: {ex}{Environment.NewLine}{Environment.NewLine}");
            try { w.Close(); } catch { }
        }
    }

    private static async Task RenderCore(Window w, string path, int waitMs, Action<Window>? afterShow)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -20000;
        w.Top = -20000;
        w.ShowActivated = false;
        w.Show();
        afterShow?.Invoke(w);
        await Task.Delay(waitMs);
        w.UpdateLayout();
        if (w.Content is FrameworkElement root)
        {
            double scale = VisualTreeHelper.GetDpi(w).DpiScaleX;
            var bmp = new RenderTargetBitmap((int)(root.ActualWidth * scale), (int)(root.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(w.Background, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            }
            bmp.Render(visual);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(path);
            enc.Save(fs);
        }
        w.Close();
    }
}
