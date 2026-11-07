using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using ScreenRecorder.Core.Interop;

namespace ScreenRecorder.App.Views;

/// <summary>
/// The red frame marking what will be recorded. In region mode it can be moved and resized;
/// it marks a picked window without handles. It never appears in the recording itself.
/// </summary>
public partial class RegionFrameWindow : Window
{
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    private const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80;
    private const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10;

    private readonly bool _locked;
    private PixelRect _initial;
    private IntPtr _hwnd;
    private const double MinSize = 64;

    public event Action? RegionChanged;

    /// <summary>True for the fixed frame around a picked window.</summary>
    public bool IsLocked => _locked;

    public RegionFrameWindow(PixelRect initial, bool locked)
    {
        InitializeComponent();
        _initial = initial;
        _locked = locked;
        if (locked) Handles.Visibility = Visibility.Collapsed;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | (locked ? WS_EX_TRANSPARENT : 0));
            Displays.ExcludeFromCapture(_hwnd);
            MoveTo(_initial);
        };
        SizeChanged += (_, _) => UpdateLabel();
        LocationChanged += (_, _) => UpdateLabel();
    }

    /// <summary>The area inside the frame, in physical screen pixels.</summary>
    public PixelRect RegionPixels
    {
        get
        {
            if (_hwnd == IntPtr.Zero) return _initial;
            Native.GetWindowRect(_hwnd, out var r);
            return new PixelRect(r.Left, r.Top, r.Width, r.Height);
        }
    }

    public void MoveTo(PixelRect r)
    {
        _initial = r;
        if (_hwnd != IntPtr.Zero) SetWindowPos(_hwnd, IntPtr.Zero, r.X, r.Y, r.Width, r.Height, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x2, SWP_NOSIZE = 0x1;

    /// <summary>Puts the frame back above other always-on-top windows.</summary>
    public void KeepOnTop()
    {
        if (_hwnd != IntPtr.Zero) SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>After a move or resize, pulls the frame back if it was dragged (mostly) off the screens.</summary>
    private void Drag_Completed(object sender, DragCompletedEventArgs e)
    {
        var r = RegionPixels;
        var fit = Displays.KeepOnScreen(r, Displays.MonitorFor(r).Bounds);
        if (fit != r)
        {
            MoveTo(fit);
            RegionChanged?.Invoke();
        }
    }

    /// <summary>While recording the frame is fixed and lets clicks through.</summary>
    public void SetRecording(bool recording)
    {
        if (_hwnd == IntPtr.Zero) return;
        Handles.Visibility = recording || _locked ? Visibility.Collapsed : Visibility.Visible;
        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        ex = recording || _locked ? ex | WS_EX_TRANSPARENT : ex & ~WS_EX_TRANSPARENT;
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
    }

    private void UpdateLabel()
    {
        var r = RegionPixels.ToEvenSize();
        MoveTab.Tag = $"{r.Width} × {r.Height}";
    }

    private void Move_Drag(object sender, DragDeltaEventArgs e)
    {
        Left += e.HorizontalChange;
        Top += e.VerticalChange;
        RegionChanged?.Invoke();
    }

    private void Resize_Drag(object sender, DragDeltaEventArgs e)
    {
        string edge = (string)((Thumb)sender).Tag;
        double left = Left, top = Top, width = Width, height = Height;
        if (double.IsNaN(width)) width = ActualWidth;
        if (double.IsNaN(height)) height = ActualHeight;
        if (edge.Contains('e')) width = Math.Max(MinSize, width + e.HorizontalChange);
        if (edge.Contains('s')) height = Math.Max(MinSize, height + e.VerticalChange);
        if (edge.Contains('w'))
        {
            double w = Math.Max(MinSize, width - e.HorizontalChange);
            left += width - w;
            width = w;
        }
        if (edge.Contains('n'))
        {
            double h = Math.Max(MinSize, height - e.VerticalChange);
            top += height - h;
            height = h;
        }
        Left = left; Top = top; Width = width; Height = height;
        RegionChanged?.Invoke();
    }
}
