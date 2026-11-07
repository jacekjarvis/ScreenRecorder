using System.Diagnostics;
using ScreenRecorder.Core.Capture;
using ScreenRecorder.Core.Encoding;
using ScreenRecorder.Core.Export;
using ScreenRecorder.Core.Interop;
using ScreenRecorder.Core.Recording;

// Developer tool for testing the engine without the UI.
//   record <seconds> <projectDir> [--pc] [--mic] [--webcam] [--keys] [--fps N] [--region x,y,w,h] [--all] [--pause]
//   export <projectDir> <out.mp4> [--start F] [--end F] [--quality Highest|High|Medium|Small] [--software]
//   info

if (args.Length == 0) { Console.WriteLine("usage: record | export | info"); return 1; }

bool Has(string flag) => args.Contains(flag);
string? Value(string flag) { int i = Array.IndexOf(args, flag); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

switch (args[0])
{
    case "info":
    {
        Console.WriteLine($"ffmpeg: {FFmpeg.Path}");
        Console.WriteLine($"encoder: {FFmpeg.BestEncoder}");
        foreach (var m in Displays.GetMonitors()) Console.WriteLine($"monitor: {m.DisplayName} {m.Bounds}");
        Console.WriteLine($"virtual: {Displays.VirtualScreen}");
        Console.WriteLine($"capture supported: {ScreenCapture.IsSupported}");
        return 0;
    }
    case "record":
    {
        double seconds = double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        string dir = Path.GetFullPath(args[2]);
        CaptureTarget target = CaptureTarget.FullScreen(Displays.GetMonitors()[0]);
        if (Has("--all")) target = CaptureTarget.AllMonitors();
        if (Value("--region") is { } r)
        {
            var p = r.Split(',').Select(int.Parse).ToArray();
            target = CaptureTarget.Region(new PixelRect(p[0], p[1], p[2], p[3]));
        }
        var options = new RecordingOptions
        {
            Target = target,
            Fps = double.Parse(Value("--fps") ?? "30", System.Globalization.CultureInfo.InvariantCulture),
            RecordPcSounds = Has("--pc"),
            RecordMicrophone = Has("--mic"),
            RecordWebcam = Has("--webcam"),
            CaptureKeystrokes = Has("--keys"),
        };
        Console.WriteLine($"Recording {target.Description} for {seconds}s into {dir} (encoder {FFmpeg.BestEncoder})");
        await using var session = await RecordingSession.StartAsync(options, dir);
        session.Failed += msg => Console.WriteLine($"FAILED: {msg}");
        var sw = Stopwatch.StartNew();
        bool paused = false;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(250);
            if (Has("--pause") && !paused && sw.Elapsed.TotalSeconds > seconds / 3)
            {
                session.Pause(); paused = true;
                Console.WriteLine("paused for 2 s");
                await Task.Delay(2000);
                session.Resume();
                seconds += 2;
            }
        }
        session.AddMarker("end marker");
        var manifest = await session.StopAsync();
        Console.WriteLine($"frames={manifest.Frames} duration={manifest.DurationSeconds:0.00}s size={manifest.Width}x{manifest.Height}");
        foreach (var w in manifest.Warnings) Console.WriteLine($"warning: {w}");
        return 0;
    }
    case "export":
    {
        string dir = Path.GetFullPath(args[1]);
        string output = Path.GetFullPath(args[2]);
        var options = new ExportOptions
        {
            StartFrame = long.Parse(Value("--start") ?? "0"),
            EndFrame = Value("--end") is { } e ? long.Parse(e) : null,
            VideoQuality = Enum.Parse<VideoQuality>(Value("--quality") ?? "Highest"),
            UseHardwareEncoder = !Has("--software"),
        };
        var sw = Stopwatch.StartNew();
        long lastPrint = 0;
        await Mp4Exporter.ExportAsync(dir, output, options, new Progress<ExportProgress>(p =>
        {
            if (p.FramesDone - lastPrint < 300 && p.FramesDone != p.FramesTotal) return;
            lastPrint = p.FramesDone;
            Console.WriteLine($"{p.FramesDone}/{p.FramesTotal} ({p.Fraction:P0})");
        }));
        Console.WriteLine($"exported {output} in {sw.Elapsed.TotalSeconds:0.0}s");
        return 0;
    }
}
Console.WriteLine("unknown command");
return 1;
