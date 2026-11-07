using ScreenRecorder.Core.Interop;
using ScreenRecorder.Core.Project;

namespace ScreenRecorder.Core.Input;

/// <summary>
/// Saves each distinct mouse cursor image once (as PNG) and hands out a numeric id for it.
/// </summary>
internal sealed class CursorShapes : IDisposable
{
    private readonly string _dir;
    private readonly JsonlWriter<CursorShape> _index;
    private readonly Dictionary<IntPtr, int> _ids = new();
    private int _next = 1;

    public CursorShapes(string projectDir)
    {
        _dir = Path.Combine(projectDir, ProjectManifest.ShapesDir);
        Directory.CreateDirectory(_dir);
        _index = new JsonlWriter<CursorShape>(Path.Combine(projectDir, ProjectManifest.ShapesFile));
    }

    public int IdFor(IntPtr hCursor)
    {
        if (hCursor == IntPtr.Zero) return 0;
        if (_ids.TryGetValue(hCursor, out int id)) return id;
        id = 0;
        try
        {
            var shape = Capture(hCursor);
            if (shape != null)
            {
                id = _next++;
                string file = $"{id}.png";
                Png.WriteBgra(Path.Combine(_dir, file), shape.Value.W, shape.Value.H, shape.Value.Pixels, shape.Value.W * 4);
                _index.Write(new CursorShape(id, shape.Value.W, shape.Value.H, shape.Value.HotX, shape.Value.HotY, $"{ProjectManifest.ShapesDir}/{file}"));
                _index.Flush();
            }
        }
        catch { id = 0; }
        _ids[hCursor] = id;
        return id;
    }

    private readonly record struct Shape(int W, int H, int HotX, int HotY, byte[] Pixels);

    /// <summary>
    /// Draws the cursor onto black and onto white; the difference gives its transparency. This handles
    /// colour cursors with alpha, classic masked cursors, and inverting ones (drawn black).
    /// </summary>
    private static unsafe Shape? Capture(IntPtr hCursor)
    {
        if (!Native.GetIconInfo(hCursor, out var info)) return null;
        try
        {
            IntPtr sizeSource = info.hbmColor != IntPtr.Zero ? info.hbmColor : info.hbmMask;
            if (Native.GetObject(sizeSource, sizeof(Native.BITMAP), out var bmp) == 0) return null;
            int w = bmp.bmWidth;
            int h = info.hbmColor != IntPtr.Zero ? bmp.bmHeight : bmp.bmHeight / 2;
            if (w <= 0 || h <= 0 || w > 256 || h > 256) return null;

            var onBlack = Render(hCursor, w, h, 0x00);
            var onWhite = Render(hCursor, w, h, 0xFF);
            var result = new byte[w * h * 4];
            for (int i = 0; i < w * h; i++)
            {
                int o = i * 4;
                int diff = (onWhite[o] - onBlack[o] + onWhite[o + 1] - onBlack[o + 1] + onWhite[o + 2] - onBlack[o + 2]) / 3;
                if (diff < -128)
                {
                    // Inverting pixel (e.g. the text I-beam): show it as solid black.
                    result[o + 3] = 255;
                    continue;
                }
                int alpha = Math.Clamp(255 - diff, 0, 255);
                if (alpha == 0) continue;
                for (int c = 0; c < 3; c++)
                    result[o + c] = (byte)Math.Clamp(onBlack[o + c] * 255 / alpha, 0, 255);
                result[o + 3] = (byte)alpha;
            }
            return new Shape(w, h, info.xHotspot, info.yHotspot, result);
        }
        finally
        {
            if (info.hbmColor != IntPtr.Zero) Native.DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero) Native.DeleteObject(info.hbmMask);
        }
    }

    private static unsafe byte[] Render(IntPtr hCursor, int w, int h, byte background)
    {
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        IntPtr dc = Native.CreateCompatibleDC(screen);
        Native.ReleaseDC(IntPtr.Zero, screen);
        var bi = new Native.BITMAPINFOHEADER
        {
            biSize = sizeof(Native.BITMAPINFOHEADER),
            biWidth = w,
            biHeight = -h, // top-down
            biPlanes = 1,
            biBitCount = 32,
        };
        IntPtr dib = Native.CreateDIBSection(dc, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = Native.SelectObject(dc, dib);
        try
        {
            var span = new Span<byte>((void*)bits, w * h * 4);
            span.Fill(background);
            Native.DrawIconEx(dc, 0, 0, hCursor, w, h, 0, IntPtr.Zero, Native.DI_NORMAL);
            return span.ToArray();
        }
        finally
        {
            Native.SelectObject(dc, old);
            Native.DeleteObject(dib);
            Native.DeleteDC(dc);
        }
    }

    public void Dispose() => _index.Dispose();
}
