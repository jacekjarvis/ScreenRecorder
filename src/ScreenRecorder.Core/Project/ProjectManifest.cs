using System.Text.Json;
using System.Text.Json.Serialization;
using ScreenRecorder.Core.Interop;

namespace ScreenRecorder.Core.Project;

/// <summary>
/// project.json — describes one recording. Every track is timed against the same clock,
/// with t = 0 at the first video frame and paused time removed.
/// </summary>
public sealed class ProjectManifest
{
    public const string FileName = "project.json";
    public const string ScreenFile = "screen.mkv";
    public const string SystemAudioFile = "system.pcm";
    public const string MicFile = "mic.pcm";
    public const string WebcamFile = "webcam.mkv";
    public const string CursorFile = "cursor.jsonl";
    public const string MouseFile = "mouse.jsonl";
    public const string KeysFile = "keys.jsonl";
    public const string ShapesDir = "cursors";
    public const string ShapesFile = "cursors.jsonl";

    public int Version { get; set; } = 1;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string CaptureMode { get; set; } = "";
    public string CaptureDescription { get; set; } = "";
    public PixelRect CaptureBounds { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public long Frames { get; set; }
    public double DurationSeconds { get; set; }
    public string Encoder { get; set; } = "";
    public bool Completed { get; set; }

    public TrackInfo? SystemAudio { get; set; }
    public TrackInfo? Microphone { get; set; }
    public WebcamTrack? Webcam { get; set; }
    public bool HasKeystrokes { get; set; }

    /// <summary>The part of the recording kept by the editor (frames, inclusive). Null = all of it.</summary>
    public FrameRange? Trim { get; set; }

    public List<Marker> Markers { get; set; } = new();
    public List<Note> Notes { get; set; } = new();
    public List<string> Warnings { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Save(string projectDir)
    {
        string path = Path.Combine(projectDir, FileName);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
        File.Move(tmp, path, overwrite: true);
    }

    public static ProjectManifest Load(string projectDir) =>
        JsonSerializer.Deserialize<ProjectManifest>(File.ReadAllText(Path.Combine(projectDir, FileName)), Json)
        ?? throw new InvalidDataException("project.json is empty.");
}

/// <summary>Raw signed 16-bit little-endian PCM.</summary>
public sealed record TrackInfo(string File, string DeviceName, int SampleRate, int Channels);
public sealed record WebcamTrack(string File, string DeviceName, int Width, int Height, double Fps);
public sealed record Marker(double Time, string Label);

/// <summary>A run of frames, both ends included.</summary>
public sealed record FrameRange(long Start, long End)
{
    public long Count => End - Start + 1;
}
public sealed record Note(double Time, string Text);

/// <summary>One cursor sample per video frame. X/Y are the hotspot relative to the captured area.</summary>
public sealed record CursorSample(
    [property: JsonPropertyName("f")] long Frame,
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("s")] int Shape,
    [property: JsonPropertyName("v")] bool Visible);

public sealed record CursorShape(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("w")] int Width,
    [property: JsonPropertyName("h")] int Height,
    [property: JsonPropertyName("hx")] int HotspotX,
    [property: JsonPropertyName("hy")] int HotspotY,
    [property: JsonPropertyName("file")] string File);

/// <summary>Mouse button or wheel event. Kind: down, up, wheel. Button: left, right, middle, x1, x2.</summary>
public sealed record MouseEvent(
    [property: JsonPropertyName("t")] double Time,
    [property: JsonPropertyName("k")] string Kind,
    [property: JsonPropertyName("b")] string? Button,
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("d")] int? WheelDelta = null);

public sealed record KeyEvent(
    [property: JsonPropertyName("t")] double Time,
    [property: JsonPropertyName("vk")] int VirtualKey,
    [property: JsonPropertyName("down")] bool Down,
    [property: JsonPropertyName("name")] string Name);
