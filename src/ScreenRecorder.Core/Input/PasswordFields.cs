using System.Runtime.InteropServices;

namespace ScreenRecorder.Core.Input;

/// <summary>
/// Detects whether keyboard focus is in a classic Windows password box, so captured keystrokes
/// can be replaced with "*". Like FlashBack, this can't see password fields inside web pages.
/// </summary>
internal static class PasswordFields
{
    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public Interop.Native.RECT rcCaret;
    }

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);

    private const int GWL_STYLE = -16;
    private const int ES_PASSWORD = 0x20;
    private const uint EM_GETPASSWORDCHAR = 0xD2;

    public static bool IsFocusedFieldPassword()
    {
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(0, ref info) || info.hwndFocus == IntPtr.Zero) return false;
        if ((Interop.Native.GetWindowLong(info.hwndFocus, GWL_STYLE) & ES_PASSWORD) != 0) return true;
        // SMTO_ABORTIFHUNG; 20 ms so a hung app can't stall the keyboard hook.
        return SendMessageTimeout(info.hwndFocus, EM_GETPASSWORDCHAR, IntPtr.Zero, IntPtr.Zero, 0x2, 20, out IntPtr ch) != IntPtr.Zero
               && ch != IntPtr.Zero;
    }
}
