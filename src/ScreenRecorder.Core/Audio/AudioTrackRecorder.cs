using NAudio.CoreAudioApi;
using NAudio.Wave;
using ScreenRecorder.Core.Timing;

// The classic WasapiCapture API is marked obsolete in NAudio 3 but remains supported and is all we need.
#pragma warning disable CS0618

namespace ScreenRecorder.Core.Audio;

/// <summary>
/// Records one audio source (PC sounds via loopback, or a microphone) to raw 16-bit PCM
/// (no 4 GB WAV limit, and nothing is lost if the PC crashes) that lines up sample-for-sample
/// with the recording clock.
/// </summary>
public sealed class AudioTrackRecorder : IDisposable
{
    private readonly RecordingClock _clock;
    private readonly WasapiCapture _capture;
    private readonly FileStream _writer;
    private readonly int _inChannels;
    private readonly int _outChannels;
    private readonly int _sampleRate;
    private readonly SampleKind _kind;
    private readonly int _bytesPerSample;
    private readonly object _gate = new();
    private short[] _scratch = new short[4096];
    private long _framesWritten;
    private bool _finished;

    public string DeviceName { get; }
    public int SampleRate => _sampleRate;
    public int Channels => _outChannels;
    public string Path { get; }

    /// <summary>Peak level of the most recent buffer, 0..1 (for level meters).</summary>
    public float Level { get; private set; }
    public Exception? Error { get; private set; }

    private enum SampleKind { Float32, Pcm16, Pcm24, Pcm32 }
    private static readonly Guid IeeeFloatSubtype = new("00000003-0000-0010-8000-00aa00389b71");

    public AudioTrackRecorder(string? deviceId, bool pcSounds, string path, RecordingClock clock)
    {
        _clock = clock;
        Path = path;
        var device = AudioDevices.Open(deviceId, output: pcSounds);
        DeviceName = device.FriendlyName;
        _capture = pcSounds ? new WasapiLoopbackCapture(device) : new WasapiCapture(device, true, 20);

        var fmt = _capture.WaveFormat;
        _inChannels = fmt.Channels;
        _sampleRate = fmt.SampleRate;
        _bytesPerSample = fmt.BitsPerSample / 8;
        _kind = Classify(fmt);
        _outChannels = Math.Min(2, _inChannels);
        _writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 256 * 1024);

        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += (_, e) => { if (e.Exception != null) Error = e.Exception; };
    }

    private static SampleKind Classify(WaveFormat fmt)
    {
        bool isFloat = fmt.Encoding == WaveFormatEncoding.IeeeFloat ||
                       (fmt is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubtype);
        if (isFloat && fmt.BitsPerSample == 32) return SampleKind.Float32;
        return fmt.BitsPerSample switch
        {
            16 => SampleKind.Pcm16,
            24 => SampleKind.Pcm24,
            32 => SampleKind.Pcm32,
            _ => throw new NotSupportedException($"Unsupported audio format: {fmt}"),
        };
    }

    public void Start() => _capture.StartRecording();

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || !_clock.IsStarted || _clock.IsPaused) return;
        double end = _clock.Elapsed.TotalSeconds;
        int frames = e.BytesRecorded / (_bytesPerSample * _inChannels);
        lock (_gate)
        {
            if (_finished) return;
            int needed = frames * _outChannels;
            if (_scratch.Length < needed) _scratch = new short[needed];
            Level = Convert(e.Buffer.AsSpan(0, e.BytesRecorded), frames, _scratch);

            var d = AudioAligner.Align(_framesWritten, frames, end, _sampleRate);
            if (d.PadFrames > 0) WriteSilence(d.PadFrames);
            int skip = d.SkipFrames;
            if (skip < frames)
            {
                var span = _scratch.AsSpan(skip * _outChannels, (frames - skip) * _outChannels);
                _writer.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(span));
                _framesWritten += frames - skip;
            }
        }
    }

    /// <summary>Converts to 16-bit with at most two channels (5.1/7.1 is folded down to stereo). Returns the peak.</summary>
    private float Convert(ReadOnlySpan<byte> input, int frames, Span<short> output)
    {
        float peak = 0;
        for (int f = 0; f < frames; f++)
        {
            int baseOffset = f * _inChannels * _bytesPerSample;
            float l = Sample(input, baseOffset, 0);
            float r = _inChannels > 1 ? Sample(input, baseOffset, 1) : l;
            if (_inChannels >= 3)
            {
                float c = Sample(input, baseOffset, 2) * 0.707f;
                l += c; r += c;
            }
            peak = Math.Max(peak, Math.Max(Math.Abs(l), Math.Abs(r)));
            output[f * _outChannels] = ToShort(l);
            if (_outChannels == 2) output[f * 2 + 1] = ToShort(r);
        }
        return Math.Min(1f, peak);
    }

    private float Sample(ReadOnlySpan<byte> input, int frameOffset, int channel)
    {
        int o = frameOffset + channel * _bytesPerSample;
        return _kind switch
        {
            SampleKind.Float32 => BitConverter.ToSingle(input.Slice(o, 4)),
            SampleKind.Pcm16 => BitConverter.ToInt16(input.Slice(o, 2)) / 32768f,
            SampleKind.Pcm24 => ((input[o] | (input[o + 1] << 8) | (input[o + 2] << 16)) << 8 >> 8) / 8388608f,
            _ => BitConverter.ToInt32(input.Slice(o, 4)) / 2147483648f,
        };
    }

    private static short ToShort(float v) => (short)Math.Clamp((int)MathF.Round(v * 32767f), short.MinValue, short.MaxValue);

    private void WriteSilence(long frames)
    {
        var zeros = new byte[Math.Min(frames, 48_000) * _outChannels * 2];
        while (frames > 0)
        {
            long n = Math.Min(frames, zeros.Length / (_outChannels * 2));
            _writer.Write(zeros, 0, (int)(n * _outChannels * 2));
            frames -= n;
            _framesWritten += n;
        }
    }

    /// <summary>Stops recording and pads the file with silence so it is exactly <paramref name="duration"/> long.</summary>
    public void Finish(TimeSpan duration)
    {
        try { _capture.StopRecording(); } catch { }
        lock (_gate)
        {
            if (_finished) return;
            long target = (long)Math.Round(duration.TotalSeconds * _sampleRate);
            if (target > _framesWritten) WriteSilence(target - _framesWritten);
            _finished = true;
            _writer.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_finished) { _finished = true; _writer.Dispose(); }
        }
        _capture.Dispose();
    }
}
