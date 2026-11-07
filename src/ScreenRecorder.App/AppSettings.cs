using System.IO;
using System.Text.Json;
using ScreenRecorder.Core.Encoding;
using ScreenRecorder.Core.Interop;

namespace ScreenRecorder.App;

public enum AreaKind { FullScreen, AllMonitors, Window, Region }
public enum MinimizeMode { Tray, FloatingControls }
public enum ScreenEdge { Top, Bottom, Left, Right }
public enum SizeLimitMode { Unlimited, StopAtSize }

/// <summary>Everything the user can set, saved to %AppData%\ScreenRecorder\settings.json.</summary>
public sealed class AppSettings
{
    // Main window
    public AreaKind Area { get; set; } = AreaKind.FullScreen;
    public string? MonitorDevice { get; set; }
    public PixelRect? LastRegion { get; set; }
    public bool RecordMicrophone { get; set; }
    public bool RecordPcSounds { get; set; } = true;
    public bool RecordWebcam { get; set; }

    // Window tab
    public MinimizeMode MinimizeTo { get; set; } = MinimizeMode.Tray;
    public bool MinimizeWhileRecording { get; set; } = true;

    // Video tab
    public int Fps { get; set; } = 30;

    // Sound tab
    public string? MicrophoneDeviceId { get; set; }
    public string? PcSoundsDeviceId { get; set; }

    // Webcam tab
    public string? WebcamDeviceId { get; set; }

    // Display tab
    public bool HideDesktopIcons { get; set; }

    // Keystrokes tab
    public bool CaptureKeystrokes { get; set; }
    public bool MaskPasswords { get; set; } = true;

    // Hotkeys tab
    public string HotkeyStartStop { get; set; } = "Ctrl+Shift+F9";
    public string HotkeyPause { get; set; } = "Ctrl+Shift+F10";
    public string HotkeyControls { get; set; } = "Ctrl+Shift+F11";
    public string HotkeyMarker { get; set; } = "Ctrl+Shift+M";

    // File size tab
    public SizeLimitMode SizeLimit { get; set; } = SizeLimitMode.Unlimited;
    public int SizeLimitMb { get; set; } = 4096;

    // Record-time notes tab
    public bool NotesEnabled { get; set; }
    public ScreenEdge NotesEdge { get; set; } = ScreenEdge.Top;
    public bool NotesPauseWhileOpen { get; set; } = true;
    public bool NotesSaveOnLeave { get; set; } = true;

    // Files
    public string OutputFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ScreenRecorder");
    public List<string> RecentRecordings { get; set; } = new();

    // Export defaults (highest by default)
    public VideoQuality ExportVideoQuality { get; set; } = VideoQuality.Highest;
    public AudioQuality ExportAudioQuality { get; set; } = AudioQuality.Highest;
    public bool ExportUseHardware { get; set; } = true;
    public string? LastExportFolder { get; set; }

    // Set Timer (scheduled recording); switches itself off after it runs
    public bool ScheduleEnabled { get; set; }
    public DateTime ScheduleStart { get; set; } = DateTime.Now.AddMinutes(10);
    public bool ScheduleStopByDuration { get; set; } = true;
    public int ScheduleDurationMinutes { get; set; } = 60;
    public DateTime ScheduleStop { get; set; } = DateTime.Now.AddMinutes(70);

    // ---- persistence ----
    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenRecorder");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch { /* corrupt settings: start fresh */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch { }
    }

    public void AddRecent(string projectDir)
    {
        RecentRecordings.Remove(projectDir);
        RecentRecordings.Insert(0, projectDir);
        if (RecentRecordings.Count > 8) RecentRecordings.RemoveRange(8, RecentRecordings.Count - 8);
        Save();
    }
}
