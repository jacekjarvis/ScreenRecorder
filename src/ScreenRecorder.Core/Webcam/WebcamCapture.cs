using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace ScreenRecorder.Core.Webcam;

public sealed record WebcamDevice(string Id, string Name);

/// <summary>Grabs webcam frames as BGRA. The latest frame is kept and copied out on demand.</summary>
public sealed class WebcamCapture : IAsyncDisposable
{
    private readonly MediaCapture _capture;
    private readonly MediaFrameReader _reader;
    private readonly object _gate = new();
    private readonly byte[] _latest;
    private readonly byte[] _scratch;
    private bool _hasFrame;

    public string Name { get; }
    public int Width { get; }
    public int Height { get; }
    public double Fps { get; }

    private WebcamCapture(MediaCapture capture, MediaFrameReader reader, string name, int width, int height, double fps)
    {
        _capture = capture;
        _reader = reader;
        Name = name;
        Width = width;
        Height = height;
        Fps = fps;
        _latest = new byte[width * height * 4];
        _scratch = new byte[width * height * 4];
        _reader.FrameArrived += OnFrame;
    }

    public static async Task<IReadOnlyList<WebcamDevice>> ListAsync()
    {
        var groups = await MediaFrameSourceGroup.FindAllAsync();
        return groups
            .Where(g => g.SourceInfos.Any(IsColorVideo))
            .Select(g => new WebcamDevice(g.Id, g.DisplayName))
            .ToList();
    }

    private static bool IsColorVideo(MediaFrameSourceInfo i) =>
        i.SourceKind == MediaFrameSourceKind.Color &&
        (i.MediaStreamType == MediaStreamType.VideoRecord || i.MediaStreamType == MediaStreamType.VideoPreview);

    /// <summary>Opens the camera (the first one when <paramref name="deviceId"/> is empty) at up to 1280x720.</summary>
    public static async Task<WebcamCapture> OpenAsync(string? deviceId)
    {
        var groups = await MediaFrameSourceGroup.FindAllAsync();
        var group = groups.FirstOrDefault(g => g.Id == deviceId && g.SourceInfos.Any(IsColorVideo))
                    ?? groups.FirstOrDefault(g => g.SourceInfos.Any(IsColorVideo))
                    ?? throw new InvalidOperationException("No webcam was found.");

        var capture = new MediaCapture();
        await capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            SourceGroup = group,
            SharingMode = MediaCaptureSharingMode.ExclusiveControl,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            StreamingCaptureMode = StreamingCaptureMode.Video,
        });

        var source = capture.FrameSources.Values.First(s => IsColorVideo(s.Info));
        var format = source.SupportedFormats
            .Where(f => f.VideoFormat.Width <= 1280 && f.VideoFormat.Height <= 720 && f.VideoFormat.Width > 0)
            .OrderByDescending(f => FpsOf(f) >= 24)
            .ThenByDescending(f => f.VideoFormat.Width * f.VideoFormat.Height)
            .ThenByDescending(FpsOf)
            .FirstOrDefault() ?? source.CurrentFormat;
        if (format != source.CurrentFormat) await source.SetFormatAsync(format);

        int width = (int)format.VideoFormat.Width & ~1, height = (int)format.VideoFormat.Height & ~1;
        double fps = Math.Clamp(FpsOf(format), 5, 60);
        var reader = await capture.CreateFrameReaderAsync(source, MediaEncodingSubtypes.Bgra8);
        reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
        var cam = new WebcamCapture(capture, reader, group.DisplayName, width, height, fps);
        var status = await reader.StartAsync();
        if (status != MediaFrameReaderStartStatus.Success)
        {
            await cam.DisposeAsync();
            throw new InvalidOperationException($"The webcam could not start ({status}).");
        }
        return cam;
    }

    private static double FpsOf(MediaFrameFormat f) =>
        f.FrameRate.Denominator == 0 ? 30 : (double)f.FrameRate.Numerator / f.FrameRate.Denominator;

    private void OnFrame(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        using var frame = sender.TryAcquireLatestFrame();
        var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (bitmap == null) return;
        SoftwareBitmap? converted = null;
        try
        {
            if (bitmap.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
                bitmap = converted = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            if (bitmap.PixelWidth < Width || bitmap.PixelHeight < Height) return;
            int srcStride = bitmap.PixelWidth * 4;
            var full = srcStride * bitmap.PixelHeight == _scratch.Length ? _scratch : new byte[srcStride * bitmap.PixelHeight];
            bitmap.CopyToBuffer(full.AsBuffer());
            lock (_gate)
            {
                for (int y = 0; y < Height; y++)
                    Buffer.BlockCopy(full, y * srcStride, _latest, y * Width * 4, Width * 4);
                _hasFrame = true;
            }
        }
        finally
        {
            converted?.Dispose();
            bitmap.Dispose();
        }
    }

    /// <summary>Copies the newest frame (black until the first one arrives).</summary>
    public void CopyLatest(Span<byte> bgra)
    {
        lock (_gate)
        {
            if (_hasFrame) _latest.CopyTo(bgra);
            else bgra[.._latest.Length].Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _reader.StopAsync(); } catch { }
        _reader.Dispose();
        _capture.Dispose();
    }
}
