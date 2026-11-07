using System.Runtime.InteropServices;
using ScreenRecorder.Core.Interop;

namespace ScreenRecorder.App.Services;

/// <summary>
/// "Click the window you want to record": waits for the next left click anywhere, swallows it,
/// and returns the top-level window under the mouse. Esc or a right click cancels.
/// </summary>
internal static class WindowPicker
{
    private delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Native.POINT p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandle(string? name);
    [StructLayout(LayoutKind.Sequential)] private struct MSLL { public Native.POINT pt; public uint data, flags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] private struct KBLL { public uint vk, scan, flags, time; public IntPtr extra; }

    private const uint GA_ROOT = 2;

    public static Task<IntPtr> PickAsync()
    {
        var tcs = new TaskCompletionSource<IntPtr>();
        IntPtr mouseHook = IntPtr.Zero, keyHook = IntPtr.Zero;
        HookProc? mouseProc = null, keyProc = null;

        void Finish(IntPtr result)
        {
            if (mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(mouseHook); mouseHook = IntPtr.Zero; }
            if (keyHook != IntPtr.Zero) { UnhookWindowsHookEx(keyHook); keyHook = IntPtr.Zero; }
            tcs.TrySetResult(result);
            GC.KeepAlive(mouseProc);
            GC.KeepAlive(keyProc);
        }

        mouseProc = (code, w, l) =>
        {
            if (code >= 0)
            {
                int msg = (int)w;
                if (msg == 0x0201 || msg == 0x0202) // left down / up: swallow both
                {
                    if (msg == 0x0202)
                    {
                        var info = Marshal.PtrToStructure<MSLL>(l);
                        IntPtr hwnd = GetAncestor(WindowFromPoint(info.pt), GA_ROOT);
                        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => Finish(hwnd));
                    }
                    return (IntPtr)1;
                }
                if (msg == 0x0204) // right down = cancel
                {
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(() => Finish(IntPtr.Zero));
                    return (IntPtr)1;
                }
            }
            return CallNextHookEx(IntPtr.Zero, code, w, l);
        };
        keyProc = (code, w, l) =>
        {
            if (code >= 0 && (int)w == 0x0100 && Marshal.PtrToStructure<KBLL>(l).vk == 0x1B)
            {
                System.Windows.Application.Current.Dispatcher.BeginInvoke(() => Finish(IntPtr.Zero));
                return (IntPtr)1;
            }
            return CallNextHookEx(IntPtr.Zero, code, w, l);
        };
        // Low-level hooks run on the installing (UI) thread's message loop.
        mouseHook = SetWindowsHookEx(14, mouseProc, GetModuleHandle(null), 0);
        keyHook = SetWindowsHookEx(13, keyProc, GetModuleHandle(null), 0);
        if (mouseHook == IntPtr.Zero) Finish(IntPtr.Zero);
        return tcs.Task;
    }
}
