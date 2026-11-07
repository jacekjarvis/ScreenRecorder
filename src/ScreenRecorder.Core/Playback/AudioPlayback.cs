using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ScreenRecorder.Core.Project;

#pragma warning disable CS0618 // WasapiOut is marked obsolete in NAudio 3 but remains supported.

namespace ScreenRecorder.Core.Playback;

/// <summary>Plays the recording's audio tracks (mixed) from a given time, for the editor preview.</summary>
public sealed class AudioPlayback : IDisposable
{
    private readonly WasapiOut? _out;
    private readonly List<Stream> _streams = new();

    public AudioPlayback(string projectDir, ProjectManifest m, double startSeconds, double durationSeconds,
        bool pcSounds = true, bool microphone = true)
    {
        const int rate = 48000;
        var mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(rate, 2)) { ReadFully = false };
        void Add(TrackInfo? t, bool include)
        {
            if (t == null || !include) return;
            string path = Path.Combine(projectDir, t.File);
            if (!File.Exists(path)) return;
            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            _streams.Add(fs);
            var raw = new RawSourceWaveStream(fs, new WaveFormat(t.SampleRate, 16, t.Channels));
            long offset = (long)(startSeconds * t.SampleRate) * t.Channels * 2;
            if (offset >= fs.Length) return;
            raw.Position = offset;
            ISampleProvider sp = raw.ToSampleProvider();
            if (t.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
            if (t.SampleRate != rate) sp = new WdlResamplingSampleProvider(sp, rate);
            mixer.AddMixerInput(new OffsetSampleProvider(sp) { Take = TimeSpan.FromSeconds(Math.Max(0, durationSeconds)) });
        }
        Add(m.SystemAudio, pcSounds);
        Add(m.Microphone, microphone);
        if (mixer.MixerInputs.Any())
        {
            try
            {
                _out = new WasapiOut(AudioClientShareMode.Shared, 80);
                _out.Init(mixer);
            }
            catch
            {
                _out?.Dispose();
                _out = null;
            }
        }
    }

    public void Play() => _out?.Play();

    public void Dispose()
    {
        try { _out?.Stop(); } catch { }
        _out?.Dispose();
        foreach (var s in _streams) s.Dispose();
    }
}
