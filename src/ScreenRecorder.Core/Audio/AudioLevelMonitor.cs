using NAudio.CoreAudioApi;
using NAudio.Wave;

// The classic WasapiCapture API is marked obsolete in NAudio 3 but remains supported and is all we need.
#pragma warning disable CS0618

namespace ScreenRecorder.Core.Audio;

/// <summary>
/// Listens to a microphone or the PC sounds without recording anything, so the Recorder can show a
/// live level meter ("is my mic working?") before a recording starts.
/// </summary>
public sealed class AudioLevelMonitor : IDisposable
{
    private readonly WasapiCapture _capture;
    private readonly WaveFormat _format;
    private readonly bool _isFloat;
    private float _peak;
    private readonly object _gate = new();

    public string DeviceName { get; }

    public AudioLevelMonitor(string? deviceId, bool pcSounds)
    {
        var device = AudioDevices.Open(deviceId, output: pcSounds);
        DeviceName = device.FriendlyName;
        _capture = pcSounds ? new WasapiLoopbackCapture(device) : new WasapiCapture(device, true, 20);
        _format = _capture.WaveFormat;
        _isFloat = _format.Encoding == WaveFormatEncoding.IeeeFloat ||
                   (_format is WaveFormatExtensible ext && ext.SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
        _capture.DataAvailable += OnData;
        _capture.StartRecording();
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        float peak = Peak(e.Buffer.AsSpan(0, e.BytesRecorded));
        lock (_gate) _peak = Math.Max(_peak, peak);
    }

    private float Peak(ReadOnlySpan<byte> data)
    {
        int bytes = _format.BitsPerSample / 8;
        float peak = 0;
        for (int i = 0; i + bytes <= data.Length; i += bytes)
        {
            float v = bytes switch
            {
                4 when _isFloat => BitConverter.ToSingle(data.Slice(i, 4)),
                2 => BitConverter.ToInt16(data.Slice(i, 2)) / 32768f,
                3 => ((data[i] | (data[i + 1] << 8) | (data[i + 2] << 16)) << 8 >> 8) / 8388608f,
                4 => BitConverter.ToInt32(data.Slice(i, 4)) / 2147483648f,
                _ => 0,
            };
            peak = Math.Max(peak, Math.Abs(v));
        }
        return Math.Min(1f, peak);
    }

    /// <summary>The loudest sample (0..1) heard since the last call. 0 when it's silent (PC sounds sends nothing then).</summary>
    public float TakePeak()
    {
        lock (_gate)
        {
            float p = _peak;
            _peak = 0;
            return p;
        }
    }

    public void Dispose()
    {
        try { _capture.StopRecording(); } catch { }
        _capture.Dispose();
    }
}
