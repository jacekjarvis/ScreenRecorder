using ScreenRecorder.Core.Audio;
using ScreenRecorder.Core.Encoding;
using ScreenRecorder.Core.Export;
using ScreenRecorder.Core.Interop;
using ScreenRecorder.Core.Project;
using ScreenRecorder.Core.Timing;

namespace ScreenRecorder.Tests;

public class RecordingClockTests
{
    private long _now;
    private RecordingClock NewClock() => new(() => _now, 1000);

    [Fact]
    public void Excludes_paused_time()
    {
        var c = NewClock();
        Assert.Equal(TimeSpan.Zero, c.Elapsed);
        c.Start();
        _now = 2000;
        c.Pause();
        _now = 5000;
        Assert.Equal(2.0, c.Elapsed.TotalSeconds, 3);
        c.Resume();
        _now = 6000;
        Assert.Equal(3.0, c.Elapsed.TotalSeconds, 3);
    }

    [Fact]
    public void Pause_and_resume_are_idempotent()
    {
        var c = NewClock();
        c.Start();
        _now = 1000;
        c.Pause(); c.Pause();
        _now = 3000;
        c.Resume(); c.Resume();
        _now = 4000;
        Assert.Equal(2.0, c.Elapsed.TotalSeconds, 3);
    }
}

public class AudioAlignerTests
{
    [Fact]
    public void In_step_buffer_is_written_as_is() =>
        Assert.Equal(new AudioAligner.Decision(0, 0), AudioAligner.Align(48_000, 480, 1.01, 48_000));

    [Fact]
    public void Gap_is_filled_with_silence()
    {
        // 1 s written, but this 10 ms buffer ended at 3.01 s: 2 s of silence first.
        var d = AudioAligner.Align(48_000, 480, 3.01, 48_000);
        Assert.Equal(96_000, d.PadFrames);
        Assert.Equal(0, d.SkipFrames);
    }

    [Fact]
    public void Overlap_is_trimmed()
    {
        var d = AudioAligner.Align(48_000, 4_800, 0.6, 48_000);
        Assert.Equal(0, d.PadFrames);
        Assert.Equal(4_800, d.SkipFrames);
    }
}

public class PngTests
{
    [Fact]
    public void Round_trips_bgra_pixels()
    {
        var px = new byte[3 * 2 * 4];
        for (int i = 0; i < px.Length; i++) px[i] = (byte)(i * 11);
        string path = Path.GetTempFileName();
        try
        {
            Png.WriteBgra(path, 3, 2, px, 12);
            var (w, h, back) = Png.ReadBgra(path);
            Assert.Equal((3, 2), (w, h));
            Assert.Equal(px, back);
        }
        finally { File.Delete(path); }
    }
}

public class RecordingFileTests
{
    [Fact]
    public void Saves_as_jsrec_named_after_the_folder_and_reopens_from_the_file()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sr-test " + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        try
        {
            new ProjectManifest { Width = 640, Height = 480, Fps = 30, Trim = new FrameRange(10, 20) }.Save(dir);
            string file = Path.Combine(dir, Path.GetFileName(dir) + ProjectManifest.Extension);
            Assert.True(File.Exists(file));
            Assert.Equal(dir, ProjectManifest.ResolveDirectory(file));
            Assert.Equal(dir, ProjectManifest.ResolveDirectory(dir));
            Assert.Equal(new FrameRange(10, 20), ProjectManifest.Load(dir).Trim);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Old_project_json_still_opens_and_is_upgraded_on_save()
    {
        string dir = Directory.CreateTempSubdirectory("sr-legacy").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, ProjectManifest.LegacyFileName), "{\"width\":800,\"height\":600,\"fps\":25}");
            var m = ProjectManifest.Load(dir);
            Assert.Equal(800, m.Width);
            m.Save(dir);
            Assert.False(File.Exists(Path.Combine(dir, ProjectManifest.LegacyFileName)));
            Assert.Single(Directory.GetFiles(dir, "*" + ProjectManifest.Extension));
            Assert.Null(ProjectManifest.ResolveDirectory(Path.Combine(dir, "nothing.txt")));
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class PixelRectTests
{
    [Fact]
    public void Intersect_and_even_size()
    {
        var a = new PixelRect(0, 0, 100, 100);
        var b = new PixelRect(50, -10, 100, 30);
        Assert.Equal(new PixelRect(50, 0, 50, 20), a.Intersect(b));
        Assert.True(a.Intersect(new PixelRect(200, 0, 5, 5)).IsEmpty);
        Assert.Equal(new PixelRect(1, 1, 100, 32), new PixelRect(1, 1, 101, 33).ToEvenSize());
    }
}

public class ExporterArgumentTests
{
    [Fact]
    public void Seek_lands_on_the_requested_frame() =>
        Assert.Equal((30 - 0.5) / 30, Mp4Exporter.SeekSeconds(30, 30), 6);

    [Fact]
    public void Two_audio_tracks_are_mixed_and_bitrate_follows_quality()
    {
        var m = new ProjectManifest
        {
            Width = 1920, Height = 1080, Fps = 30,
            SystemAudio = new TrackInfo("system.pcm", "Speakers", 48000, 2),
            Microphone = new TrackInfo("mic.pcm", "Mic", 48000, 1),
        };
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "system.pcm"), new byte[16]);
            File.WriteAllBytes(Path.Combine(dir, "mic.pcm"), new byte[16]);
            string args = Mp4Exporter.BuildEncoderInputs(dir, m, new ExportOptions { AudioQuality = AudioQuality.Medium }, 1, 2, 1920);
            Assert.Contains("amix=inputs=2", args);
            Assert.Contains("-b:a 192k", args);
            Assert.Contains("-ac 1", args);
            Assert.Contains("-ss 1 -t 2", args);

            string noMic = Mp4Exporter.BuildEncoderInputs(dir, m, new ExportOptions { IncludeMicrophone = false }, 0, 2, 1920);
            Assert.DoesNotContain("amix", noMic);
            Assert.Contains("-map 1:a", noMic);
            Assert.Contains("-b:a 320k", noMic);
        }
        finally { Directory.Delete(dir, true); }
    }
}
