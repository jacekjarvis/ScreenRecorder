using ScreenRecorder.Core.Capture;

namespace ScreenRecorder.Core.Recording;

public sealed class RecordingOptions
{
    public required CaptureTarget Target { get; init; }
    public double Fps { get; init; } = 30;

    public bool RecordPcSounds { get; init; }
    public string? PcSoundsDeviceId { get; init; }
    public bool RecordMicrophone { get; init; }
    public string? MicrophoneDeviceId { get; init; }

    public bool RecordWebcam { get; init; }
    public string? WebcamDeviceId { get; init; }

    public bool CaptureKeystrokes { get; init; }
    public bool MaskPasswords { get; init; } = true;

    /// <summary>Stop once the project folder reaches this size. Null = unlimited.</summary>
    public long? SizeLimitBytes { get; init; }
}
