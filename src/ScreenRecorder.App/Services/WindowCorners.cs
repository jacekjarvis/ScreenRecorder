using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ScreenRecorder.App.Services;

/// <summary>
/// Asks Windows 11 to draw rounded corners (and a subtle border) on our custom-drawn windows.
/// Ignored on Windows 10, where windows stay square.
/// </summary>
internal static class WindowCorners
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUND = 2, DWMWCP_ROUNDSMALL = 3;

    public static void Apply(Window window)
    {
        if (window.AllowsTransparency) return; // the red region frame must keep sharp corners
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        bool small = window.WindowStyle == WindowStyle.None && window.ActualHeight < 80;
        int preference = small ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        int border = 0x00505050; // COLORREF 0x00BBGGRR: a soft grey outline
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
    }
}
