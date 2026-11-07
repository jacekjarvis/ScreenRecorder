using ScreenRecorder.Core.Interop;

namespace ScreenRecorder.Core.Desktop;

/// <summary>Hides and restores the desktop icons (for cleaner recordings).</summary>
public static class DesktopIcons
{
    private static IntPtr FindIconList()
    {
        IntPtr defView = Native.FindWindowEx(Native.FindWindow("Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
        // With a slideshow wallpaper, Explorer moves the icons into a WorkerW window.
        for (IntPtr worker = IntPtr.Zero; defView == IntPtr.Zero;)
        {
            worker = Native.FindWindowEx(IntPtr.Zero, worker, "WorkerW", null);
            if (worker == IntPtr.Zero) break;
            defView = Native.FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
        }
        return defView == IntPtr.Zero ? IntPtr.Zero : Native.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
    }

    /// <summary>Hides the icons. Returns true only if they were visible (so only then should they be restored).</summary>
    public static bool Hide()
    {
        IntPtr list = FindIconList();
        if (list == IntPtr.Zero || !Native.IsWindowVisible(list)) return false;
        Native.ShowWindow(list, Native.SW_HIDE);
        return true;
    }

    public static void Show()
    {
        IntPtr list = FindIconList();
        if (list != IntPtr.Zero) Native.ShowWindow(list, Native.SW_SHOW);
    }
}
