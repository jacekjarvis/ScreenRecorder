using System.Text;

namespace ScreenRecorder.Core.Interop;

/// <summary>A rectangle in physical screen pixels (virtual-desktop coordinates).</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public PixelRect Intersect(PixelRect other)
    {
        int x = Math.Max(X, other.X), y = Math.Max(Y, other.Y);
        int r = Math.Min(Right, other.Right), b = Math.Min(Bottom, other.Bottom);
        return r > x && b > y ? new PixelRect(x, y, r - x, b - y) : default;
    }

    /// <summary>H.264 with 4:2:0 chroma needs even dimensions.</summary>
    public PixelRect ToEvenSize() => this with { Width = Width & ~1, Height = Height & ~1 };

    public override string ToString() => $"{Width}x{Height} at ({X},{Y})";
}

public sealed record MonitorInfo(IntPtr Handle, string DeviceName, PixelRect Bounds, bool IsPrimary)
{
    public string DisplayName => $"{(IsPrimary ? "Primary" : "Monitor")} {Bounds.Width}x{Bounds.Height} ({DeviceName.TrimStart('\\', '.')})";
}

public sealed record WindowInfo(IntPtr Handle, string Title, uint ProcessId);

public static class Displays
{
    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var list = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var info = new Native.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(h, ref info))
            {
                var r = info.rcMonitor;
                list.Add(new MonitorInfo(h, info.szDevice, new PixelRect(r.Left, r.Top, r.Width, r.Height),
                    (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0));
            }
            return true;
        }, IntPtr.Zero);
        return list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.X).ToList();
    }

    public static PixelRect VirtualScreen => new(
        Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN), Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
        Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN), Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));

    /// <summary>Top-level windows a person would recognise as apps (same rules as Alt+Tab, roughly).</summary>
    public static IReadOnlyList<WindowInfo> GetWindows()
    {
        var list = new List<WindowInfo>();
        IntPtr shell = Native.GetShellWindow();
        uint ownPid = (uint)Environment.ProcessId;
        Native.EnumWindows((hwnd, _) =>
        {
            if (hwnd == shell || !Native.IsWindowVisible(hwnd)) return true;
            if ((Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            int len = Native.GetWindowTextLength(hwnd);
            if (len == 0) return true;
            var sb = new StringBuilder(len + 1);
            Native.GetWindowText(hwnd, sb, sb.Capacity);
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == ownPid) return true;
            list.Add(new WindowInfo(hwnd, sb.ToString(), pid));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The window's visible bounds (without the invisible resize border).</summary>
    public static PixelRect GetWindowBounds(IntPtr hwnd)
    {
        if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT r,
                System.Runtime.InteropServices.Marshal.SizeOf<Native.RECT>()) != 0)
            Native.GetWindowRect(hwnd, out r);
        return new PixelRect(r.Left, r.Top, r.Width, r.Height);
    }

    /// <summary>Hide a window of this app from screen capture (Windows 10 2004+).</summary>
    public static void ExcludeFromCapture(IntPtr hwnd) => Native.SetWindowDisplayAffinity(hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
}
