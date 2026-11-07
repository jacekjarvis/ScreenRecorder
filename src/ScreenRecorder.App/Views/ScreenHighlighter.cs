using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ScreenRecorder.Core.Interop;

namespace ScreenRecorder.App.Views;

/// <summary>
/// Shows which screen will be recorded, as FlashBack does when you choose it in Area: a red frame
/// around the chosen monitor (every monitor for "All monitors") and the other monitors dimmed.
/// The overlays let clicks through and never appear in a recording.
/// </summary>
internal sealed class ScreenHighlighter
{
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    private const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private readonly Window _owner;
    private readonly DispatcherTimer _hideTimer;
    private readonly List<Overlay> _overlays = new();
    private bool _ownerRaised, _ownerWasTopmost;

    public ScreenHighlighter(Window owner)
    {
        _owner = owner;
        _hideTimer = new DispatcherTimer(DispatcherPriority.Normal, owner.Dispatcher);
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); FadeOut(); };
    }

    /// <summary>
    /// Highlights the screen(s) for this choice. With <paramref name="hideAfter"/> it fades away after
    /// that long; without, it stays until <see cref="Hide"/> (e.g. while the Area list is open).
    /// Anything other than full screen / all monitors removes the highlight.
    /// </summary>
    public void Show(AreaKind kind, string? device, TimeSpan? hideAfter)
    {
        _hideTimer.Stop();
        if (kind is not (AreaKind.FullScreen or AreaKind.AllMonitors)) { Hide(); return; }
        var monitors = Displays.GetMonitors();
        if (monitors.Count == 0) return;
        var chosen = monitors.FirstOrDefault(m => m.DeviceName == device) ?? monitors[0];

        // Reuse the overlays while the monitors stay the same, so moving through the list doesn't flicker.
        if (_overlays.Count != monitors.Count || _overlays.Where((o, i) => o.Bounds != monitors[i].Bounds).Any())
        {
            CloseOverlays();
            foreach (var m in monitors) _overlays.Add(new Overlay(m.Bounds));
        }
        for (int i = 0; i < monitors.Count; i++)
        {
            var o = _overlays[i];
            o.SetChosen(kind == AreaKind.AllMonitors || monitors[i] == chosen);
            o.BeginAnimation(UIElement.OpacityProperty, null);
            o.Opacity = 1;
            if (!o.IsVisible) o.Show();
            o.BringToFront();
        }
        RaiseOwner();
        if (hideAfter is { } d)
        {
            _hideTimer.Interval = d;
            _hideTimer.Start();
        }
    }

    /// <summary>Removes the highlight at once.</summary>
    public void Hide()
    {
        _hideTimer.Stop();
        CloseOverlays();
        RestoreOwner();
    }

    private void FadeOut()
    {
        if (_overlays.Count == 0) return;
        var overlays = _overlays.ToList();
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(450));
        fade.Completed += (_, _) =>
        {
            // Only finish if nothing re-showed the highlight in the meantime.
            if (overlays.All(o => o.Opacity == 0 || !o.IsVisible) && _overlays.SequenceEqual(overlays)) Hide();
        };
        foreach (var o in overlays) o.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    private void CloseOverlays()
    {
        foreach (var o in _overlays) o.Close();
        _overlays.Clear();
    }

    /// <summary>Keeps the Recorder itself above the dimming while the highlight is up.</summary>
    private void RaiseOwner()
    {
        if (!_owner.IsVisible) return;
        if (!_ownerRaised)
        {
            _ownerWasTopmost = _owner.Topmost;
            _ownerRaised = true;
        }
        _owner.Topmost = true;
        var hwnd = new WindowInteropHelper(_owner).Handle;
        if (hwnd != IntPtr.Zero) SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void RestoreOwner()
    {
        if (!_ownerRaised) return;
        _ownerRaised = false;
        _owner.Topmost = _ownerWasTopmost;
    }

    /// <summary>One monitor's overlay: a red frame when chosen, a dark veil otherwise.</summary>
    private sealed class Overlay : Window
    {
        private static readonly Brush Red = Frozen(Color.FromRgb(0xE0, 0x26, 0x2A));
        private static readonly Brush Veil = Frozen(Color.FromArgb(0x70, 0, 0, 0));
        private readonly Border _border = new();
        private IntPtr _hwnd;

        public PixelRect Bounds { get; }

        public Overlay(PixelRect bounds)
        {
            Bounds = bounds;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Focusable = false;
            Topmost = true;
            Content = _border;
            SourceInitialized += (_, _) =>
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
                Displays.ExcludeFromCapture(_hwnd);
                Place();
            };
        }

        public void SetChosen(bool chosen)
        {
            _border.BorderBrush = Red;
            _border.BorderThickness = new Thickness(chosen ? 5 : 0);
            _border.Background = chosen ? null : Veil;
        }

        public void BringToFront() => Place();

        /// <summary>Exactly over the monitor, in physical pixels (also right on monitors with other scaling).</summary>
        private void Place()
        {
            if (_hwnd != IntPtr.Zero)
                SetWindowPos(_hwnd, HWND_TOPMOST, Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        private static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
