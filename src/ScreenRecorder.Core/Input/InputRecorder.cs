using System.Runtime.InteropServices;
using System.Text;
using ScreenRecorder.Core.Interop;
using ScreenRecorder.Core.Project;
using ScreenRecorder.Core.Timing;

namespace ScreenRecorder.Core.Input;

/// <summary>
/// Records the mouse cursor (position and image every video frame), mouse clicks and, optionally,
/// keystrokes — all as data, so they can be shown, hidden or restyled at export time.
/// </summary>
public sealed class InputRecorder : IDisposable
{
    private readonly RecordingClock _clock;
    private readonly Func<(int X, int Y)> _origin;
    private readonly JsonlWriter<CursorSample> _cursor;
    private readonly JsonlWriter<MouseEvent> _mouse;
    private readonly JsonlWriter<KeyEvent>? _keys;
    private readonly bool _maskPasswords;
    private readonly CursorShapes _shapes;
    private readonly Thread _hookThread;
    private readonly ManualResetEventSlim _hooksReady = new();
    private Native.HookProc? _mouseProc, _keyProc;   // kept alive while hooks are installed
    private uint _hookThreadId;
    private bool _disposed;

    public InputRecorder(string projectDir, RecordingClock clock, Func<(int X, int Y)> origin, bool captureKeys, bool maskPasswords)
    {
        _clock = clock;
        _origin = origin;
        _maskPasswords = maskPasswords;
        _cursor = new JsonlWriter<CursorSample>(Path.Combine(projectDir, ProjectManifest.CursorFile));
        _mouse = new JsonlWriter<MouseEvent>(Path.Combine(projectDir, ProjectManifest.MouseFile));
        if (captureKeys) _keys = new JsonlWriter<KeyEvent>(Path.Combine(projectDir, ProjectManifest.KeysFile));
        _shapes = new CursorShapes(projectDir);
        _hookThread = new Thread(HookLoop) { IsBackground = true, Name = "Input hooks" };
    }

    public void Start()
    {
        _hookThread.Start();
        _hooksReady.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Called by the screen pacer once per video frame.</summary>
    public void SampleCursor(long frame)
    {
        var ci = new Native.CURSORINFO { cbSize = Marshal.SizeOf<Native.CURSORINFO>() };
        if (!Native.GetCursorInfo(ref ci))
        {
            _cursor.Write(new CursorSample(frame, 0, 0, 0, false));
            return;
        }
        var (ox, oy) = _origin();
        bool visible = (ci.flags & Native.CURSOR_SHOWING) != 0;
        int shape = visible ? _shapes.IdFor(ci.hCursor) : 0;
        _cursor.Write(new CursorSample(frame, ci.ptScreenPos.X - ox, ci.ptScreenPos.Y - oy, shape, visible && shape != 0));
    }

    private void HookLoop()
    {
        _hookThreadId = Native.GetCurrentThreadId();
        _mouseProc = MouseHook;
        IntPtr module = Native.GetModuleHandle(null);
        IntPtr mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, _mouseProc, module, 0);
        IntPtr keyHook = IntPtr.Zero;
        if (_keys != null)
        {
            _keyProc = KeyHook;
            keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _keyProc, module, 0);
        }
        _hooksReady.Set();
        try
        {
            while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
        }
        finally
        {
            if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
            if (keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyHook);
        }
    }

    private bool Recording => _clock.IsStarted && !_clock.IsPaused;

    private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && Recording)
        {
            var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            var (kind, button) = (int)wParam switch
            {
                0x0201 => ("down", "left"), 0x0202 => ("up", "left"),
                0x0204 => ("down", "right"), 0x0205 => ("up", "right"),
                0x0207 => ("down", "middle"), 0x0208 => ("up", "middle"),
                0x020B => ("down", (data.mouseData >> 16) == 1 ? "x1" : "x2"),
                0x020C => ("up", (data.mouseData >> 16) == 1 ? "x1" : "x2"),
                0x020A => ("wheel", (string?)null),
                _ => ((string?)null, (string?)null),
            };
            if (kind != null)
            {
                var (ox, oy) = _origin();
                int? delta = kind == "wheel" ? (short)(data.mouseData >> 16) : null;
                _mouse.Write(new MouseEvent(_clock.Elapsed.TotalSeconds, kind, button, data.pt.X - ox, data.pt.Y - oy, delta));
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private IntPtr KeyHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && Recording && _keys != null)
        {
            var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            int msg = (int)wParam;
            bool down = msg == 0x0100 || msg == 0x0104;
            bool up = msg == 0x0101 || msg == 0x0105;
            if (down || up)
            {
                bool masked = _maskPasswords && IsCharacterKey((int)data.vkCode) && PasswordFields.IsFocusedFieldPassword();
                _keys.Write(masked
                    ? new KeyEvent(_clock.Elapsed.TotalSeconds, 0, down, "*")
                    : new KeyEvent(_clock.Elapsed.TotalSeconds, (int)data.vkCode, down, KeyName(data)));
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private static bool IsCharacterKey(int vk) =>
        vk is >= 0x30 and <= 0x5A or >= 0x60 and <= 0x6F or >= 0xBA and <= 0xE2 or 0x20;

    private static string KeyName(Native.KBDLLHOOKSTRUCT k)
    {
        int lParam = (int)(k.scanCode << 16);
        if ((k.flags & 1) != 0) lParam |= 1 << 24; // extended key
        var sb = new StringBuilder(64);
        return Native.GetKeyNameText(lParam, sb, sb.Capacity) > 0 ? sb.ToString() : $"VK{k.vkCode:X2}";
    }

    public void Flush()
    {
        _cursor.Flush();
        _mouse.Flush();
        _keys?.Flush();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hookThreadId != 0) Native.PostThreadMessage(_hookThreadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _hookThread.Join(TimeSpan.FromSeconds(2));
        _cursor.Dispose();
        _mouse.Dispose();
        _keys?.Dispose();
        _shapes.Dispose();
    }
}
