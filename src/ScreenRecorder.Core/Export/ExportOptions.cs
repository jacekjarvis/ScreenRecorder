using ScreenRecorder.Core.Encoding;

namespace ScreenRecorder.Core.Export;

public sealed class ExportOptions
{
    /// <summary>First frame to include (0-based).</summary>
    public long StartFrame { get; init; }
    /// <summary>Last frame to include (inclusive). Null = to the end.</summary>
    public long? EndFrame { get; init; }

    public VideoQuality VideoQuality { get; init; } = VideoQuality.Highest;
    public AudioQuality AudioQuality { get; init; } = AudioQuality.Highest;

    public bool IncludeCursor { get; init; } = true;
    public bool IncludeWebcam { get; init; } = true;
    public bool IncludePcSounds { get; init; } = true;
    public bool IncludeMicrophone { get; init; } = true;

    /// <summary>Use the graphics card's encoder when there is one (much faster for long recordings).</summary>
    public bool UseHardwareEncoder { get; init; } = true;

    public static int AudioBitrateKbps(AudioQuality q) => q switch
    {
        AudioQuality.Highest => 320,
        AudioQuality.High => 256,
        AudioQuality.Medium => 192,
        _ => 128,
    };
}

public readonly record struct ExportProgress(long FramesDone, long FramesTotal, TimeSpan Elapsed)
{
    public double Fraction => FramesTotal == 0 ? 0 : (double)FramesDone / FramesTotal;

    public TimeSpan? Remaining => FramesDone < 30 || Fraction <= 0
        ? null
        : TimeSpan.FromSeconds(Elapsed.TotalSeconds / Fraction - Elapsed.TotalSeconds);
}
