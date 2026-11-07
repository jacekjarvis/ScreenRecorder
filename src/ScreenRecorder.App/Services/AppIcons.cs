using System.Drawing;
using System.Drawing.Drawing2D;

namespace ScreenRecorder.App.Services;

/// <summary>Draws the app's tray icons (blue camera tile; red dot while recording, amber while paused).</summary>
internal static class AppIcons
{
    public enum State { Idle, Recording, Paused }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Create(State state)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var tile = RoundedRect(new RectangleF(1, 1, 30, 30), 6);
            using var blue = new SolidBrush(Color.FromArgb(0x2F, 0x7F, 0xE0));
            g.FillPath(blue, tile);
            using var white = new SolidBrush(Color.White);
            g.FillRectangle(white, 6, 9, 14, 14);
            g.FillPolygon(white, new[] { new PointF(20, 14), new PointF(26, 10), new PointF(26, 22), new PointF(20, 18) });
            var dot = state switch
            {
                State.Recording => Color.FromArgb(0xE0, 0x26, 0x2A),
                State.Paused => Color.FromArgb(0xF0, 0xA0, 0x20),
                _ => Color.FromArgb(0x2F, 0x7F, 0xE0),
            };
            using var dotBrush = new SolidBrush(dot);
            g.FillEllipse(dotBrush, 9, 12, 8, 8);
        }
        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
