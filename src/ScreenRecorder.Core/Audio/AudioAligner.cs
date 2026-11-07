namespace ScreenRecorder.Core.Audio;

/// <summary>
/// Keeps an audio track locked to the recording clock. Windows sends no data for PC sounds while
/// nothing is playing, and drops nothing for pauses, so each buffer is placed by its timestamp:
/// gaps are filled with silence and overlaps are trimmed.
/// </summary>
public static class AudioAligner
{
    public readonly record struct Decision(long PadFrames, int SkipFrames);

    /// <param name="writtenFrames">Frames already in the file.</param>
    /// <param name="bufferFrames">Frames in the buffer that just arrived.</param>
    /// <param name="bufferEndSeconds">Recording-clock time at which the buffer ended.</param>
    public static Decision Align(long writtenFrames, int bufferFrames, double bufferEndSeconds, int sampleRate, double toleranceSeconds = 0.04)
    {
        long expectedStart = (long)Math.Round(bufferEndSeconds * sampleRate) - bufferFrames;
        long drift = expectedStart - writtenFrames;
        long tolerance = (long)(toleranceSeconds * sampleRate);
        if (drift > tolerance) return new Decision(drift, 0);
        if (drift < -tolerance) return new Decision(0, (int)Math.Min(bufferFrames, -drift));
        return new Decision(0, 0);
    }
}
