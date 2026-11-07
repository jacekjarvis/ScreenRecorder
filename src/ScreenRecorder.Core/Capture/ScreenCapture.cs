using ScreenRecorder.Core.Interop;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace ScreenRecorder.Core.Capture;

/// <summary>
/// Captures a <see cref="CaptureTarget"/> into one GPU texture of fixed size. For an area, each
/// monitor that overlaps it has its own capture session; their frames are copied into place, so
/// regions can span monitors. The mouse cursor is never captured (it is recorded as data).
/// </summary>
public sealed class ScreenCapture : IDisposable
{
    private readonly CaptureTarget _target;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDirect3DDevice _winrtDevice;
    private readonly ID3D11Texture2D _composite;
    private readonly ID3D11RenderTargetView _compositeView;
    private readonly ID3D11Texture2D _staging;
    private readonly object _gpuLock = new();
    private readonly List<Source> _sources = new();
    private readonly PixelRect _bounds;
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public long FramesArrived { get; private set; }

    /// <summary>Raised on a capture thread if the window being recorded closes.</summary>
    public event Action? TargetClosed;

    private sealed class Source
    {
        public required GraphicsCaptureItem Item;
        public required Direct3D11CaptureFramePool Pool;
        public required GraphicsCaptureSession Session;
        public PixelRect ScreenBounds;   // for monitors: where the monitor sits on the desktop
        public SizeInt32 LastSize;
    }

    public static bool IsSupported => GraphicsCaptureSession.IsSupported();

    public ScreenCapture(CaptureTarget target)
    {
        _target = target;
        D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 }, out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
        _device = device!;
        _context = context!;
        using (var mt = _context.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
        _winrtDevice = WinRtInterop.CreateDirect3DDevice(_device);

        switch (target)
        {
            case CaptureTarget.Area a:
                _bounds = a.Bounds.ToEvenSize();
                if (_bounds.IsEmpty) throw new ArgumentException("The area to record is empty.");
                foreach (var m in Displays.GetMonitors())
                {
                    if (m.Bounds.Intersect(_bounds).IsEmpty) continue;
                    _sources.Add(CreateSource(WinRtInterop.CreateItemForMonitor(m.Handle), m.Bounds));
                }
                if (_sources.Count == 0) throw new ArgumentException("The area to record is not on any monitor.");
                break;
            case CaptureTarget.Window w:
                var item = WinRtInterop.CreateItemForWindow(w.Handle);
                _bounds = new PixelRect(0, 0, item.Size.Width, item.Size.Height).ToEvenSize();
                if (_bounds.IsEmpty) throw new ArgumentException("The window is too small to record.");
                var src = CreateSource(item, default);
                item.Closed += (_, _) => TargetClosed?.Invoke();
                _sources.Add(src);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target));
        }

        Width = _bounds.Width;
        Height = _bounds.Height;
        var desc = new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget,
        };
        _composite = _device.CreateTexture2D(desc);
        _compositeView = _device.CreateRenderTargetView(_composite);
        _context.ClearRenderTargetView(_compositeView, new Color4(0, 0, 0, 1));
        desc.Usage = ResourceUsage.Staging;
        desc.BindFlags = BindFlags.None;
        desc.CPUAccessFlags = CpuAccessFlags.Read;
        _staging = _device.CreateTexture2D(desc);
    }

    private Source CreateSource(GraphicsCaptureItem item, PixelRect screenBounds)
    {
        var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
        var session = pool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;
        if (ApiInformation.IsPropertyPresent(typeof(GraphicsCaptureSession).FullName, nameof(GraphicsCaptureSession.IsBorderRequired)))
        {
            try { session.IsBorderRequired = false; } catch { /* the yellow border stays; harmless */ }
        }
        var src = new Source { Item = item, Pool = pool, Session = session, ScreenBounds = screenBounds, LastSize = item.Size };
        pool.FrameArrived += (p, _) => OnFrame(src, p);
        return src;
    }

    /// <summary>Asks Windows (once) to allow capture without the yellow border. Safe to skip.</summary>
    public static async Task TryRequestBorderlessAsync()
    {
        try
        {
            if (ApiInformation.IsMethodPresent(typeof(GraphicsCaptureAccess).FullName, nameof(GraphicsCaptureAccess.RequestAccessAsync)))
                await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
        }
        catch { }
    }

    public void Start()
    {
        foreach (var s in _sources) s.Session.StartCapture();
    }

    /// <summary>Where the top-left of the movie currently is on the desktop (moves with a recorded window).</summary>
    public (int X, int Y) CurrentOrigin()
    {
        if (_target is CaptureTarget.Window w && Native.IsWindow(w.Handle))
        {
            var b = Displays.GetWindowBounds(w.Handle);
            return (b.X, b.Y);
        }
        return (_bounds.X, _bounds.Y);
    }

    public PixelRect Bounds => _target is CaptureTarget.Window w && Native.IsWindow(w.Handle)
        ? Displays.GetWindowBounds(w.Handle) with { Width = Width, Height = Height }
        : _bounds;

    private void OnFrame(Source src, Direct3D11CaptureFramePool pool)
    {
        using var frame = pool.TryGetNextFrame();
        if (frame == null || _disposed) return;
        var size = frame.ContentSize;
        using var texture = WinRtInterop.GetTexture(frame.Surface);
        lock (_gpuLock)
        {
            if (_disposed) return;
            if (_target is CaptureTarget.Window)
            {
                // Window: copy from its top-left. If it shrank, clear so old pixels don't linger.
                if (size.Width < src.LastSize.Width || size.Height < src.LastSize.Height)
                    _context.ClearRenderTargetView(_compositeView, new Color4(0, 0, 0, 1));
                int w = Math.Min(size.Width, Width), h = Math.Min(size.Height, Height);
                if (w > 0 && h > 0)
                    _context.CopySubresourceRegion(_composite, 0, 0, 0, 0, texture, 0, new Box(0, 0, 0, w, h, 1));
            }
            else
            {
                var overlap = src.ScreenBounds.Intersect(_bounds);
                if (!overlap.IsEmpty)
                {
                    int sx = overlap.X - src.ScreenBounds.X, sy = overlap.Y - src.ScreenBounds.Y;
                    int w = Math.Min(overlap.Width, size.Width - sx), h = Math.Min(overlap.Height, size.Height - sy);
                    if (w > 0 && h > 0)
                        _context.CopySubresourceRegion(_composite, 0, (uint)(overlap.X - _bounds.X), (uint)(overlap.Y - _bounds.Y), 0,
                            texture, 0, new Box(sx, sy, 0, sx + w, sy + h, 1));
                }
            }
            FramesArrived++;
        }

        if (size.Width != src.LastSize.Width || size.Height != src.LastSize.Height)
        {
            src.LastSize = size;
            pool.Recreate(_winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
        }
    }

    /// <summary>Copies the latest composite image into <paramref name="bgra"/> (tightly packed rows).</summary>
    public unsafe void CopyLatest(Span<byte> bgra)
    {
        int rowBytes = Width * 4;
        if (bgra.Length < rowBytes * Height) throw new ArgumentException("Buffer too small.");
        lock (_gpuLock)
        {
            if (_disposed) return;
            _context.CopyResource(_staging, _composite);
            var map = _context.Map(_staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                byte* src = (byte*)map.DataPointer;
                for (int y = 0; y < Height; y++)
                    new ReadOnlySpan<byte>(src + (long)y * map.RowPitch, rowBytes).CopyTo(bgra.Slice(y * rowBytes, rowBytes));
            }
            finally { _context.Unmap(_staging, 0); }
        }
    }

    public void Dispose()
    {
        lock (_gpuLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        foreach (var s in _sources)
        {
            try { s.Session.Dispose(); } catch { }
            try { s.Pool.Dispose(); } catch { }
        }
        lock (_gpuLock)
        {
            _staging.Dispose();
            _compositeView.Dispose();
            _composite.Dispose();
            _context.Dispose();
            _device.Dispose();
        }
    }
}
