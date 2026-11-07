using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ScreenRecorder.App.Views;

/// <summary>
/// The timeline under the movie: whole length from left to right, time ticks, the current frame in red,
/// the selected range highlighted, and markers. Click to jump; drag or Shift+click to select.
/// </summary>
public sealed class Timeline : FrameworkElement
{
    private long _frames = 1;
    private double _fps = 30;
    private long _current;
    private long? _selStart, _selEnd;
    private long _dragAnchor = -1;
    private IReadOnlyList<(long Frame, string Label)> _markers = Array.Empty<(long, string)>();

    private static readonly Brush Track = new SolidColorBrush(Color.FromRgb(0x1F, 0x1F, 0x1F));
    private static readonly Brush TickBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
    private static readonly Brush Selection = new SolidColorBrush(Color.FromArgb(0x90, 0x2F, 0x7F, 0xE0));
    private static readonly Brush SelectionEdge = new SolidColorBrush(Color.FromRgb(0x4A, 0x93, 0xEC));
    private static readonly Brush Current = new SolidColorBrush(Color.FromRgb(0xE0, 0x26, 0x2A));
    private static readonly Brush MarkerBrush = new SolidColorBrush(Color.FromRgb(0xF0, 0xA0, 0x20));
    private static readonly Pen TickPen = new(TickBrush, 1);
    private static readonly Typeface Face = new("Segoe UI");

    /// <summary>The user clicked or dragged: jump to this frame.</summary>
    public event Action<long>? Seek;
    public event Action? SelectionChanged;

    public long CurrentFrame { get => _current; set { _current = Math.Clamp(value, 0, _frames - 1); InvalidateVisual(); } }
    public long? SelectionStart => _selStart;
    public long? SelectionEnd => _selEnd;
    public bool HasSelection => _selStart.HasValue && _selEnd.HasValue;

    public Timeline()
    {
        Height = 40;
        Cursor = Cursors.Hand;
        Focusable = false;
        System.Windows.Controls.ToolTipService.SetInitialShowDelay(this, 0);
    }

    public void SetMovie(long frames, double fps, IReadOnlyList<(long Frame, string Label)> markers)
    {
        _frames = Math.Max(1, frames);
        _fps = fps;
        _markers = markers;
        _current = Math.Clamp(_current, 0, _frames - 1);
        ClearSelection();
        InvalidateVisual();
    }

    public void SetSelection(long start, long end)
    {
        _selStart = Math.Clamp(Math.Min(start, end), 0, _frames - 1);
        _selEnd = Math.Clamp(Math.Max(start, end), 0, _frames - 1);
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    public void ClearSelection()
    {
        if (_selStart == null && _selEnd == null) return;
        _selStart = _selEnd = null;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    private const double Pad = 8;
    private double TrackWidth => Math.Max(1, ActualWidth - 2 * Pad);
    private double XOf(long frame) => Pad + TrackWidth * frame / Math.Max(1, _frames - 1);
    private long FrameAt(double x) => (long)Math.Round(Math.Clamp((x - Pad) / TrackWidth, 0, 1) * (_frames - 1));

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        double trackTop = h - 12;
        dc.DrawRectangle(Track, null, new Rect(Pad, trackTop, TrackWidth, 6));

        // Ticks: pick a step that leaves roughly 70 px between labels.
        double seconds = _frames / _fps;
        double[] steps = { 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200 };
        double pxPerSec = TrackWidth / Math.Max(seconds, 0.001);
        double step = steps.FirstOrDefault(s => s * pxPerSec >= 70, 7200);
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (double t = 0; t <= seconds + 0.0001; t += step)
        {
            double x = Pad + t * pxPerSec;
            dc.DrawLine(TickPen, new Point(x, trackTop - 4), new Point(x, trackTop));
            var label = new FormattedText(FormatTick(t, step), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Face, 10.5, TickBrush, dpi);
            dc.DrawText(label, new Point(Math.Min(x + 2, w - label.Width), trackTop - 18));
        }

        if (HasSelection)
        {
            double x1 = XOf(_selStart!.Value), x2 = XOf(_selEnd!.Value);
            dc.DrawRectangle(Selection, null, new Rect(x1, trackTop - 2, Math.Max(2, x2 - x1), 10));
            dc.DrawRectangle(SelectionEdge, null, new Rect(x1 - 1, trackTop - 4, 2, 14));
            dc.DrawRectangle(SelectionEdge, null, new Rect(x2 - 1, trackTop - 4, 2, 14));
        }

        foreach (var (frame, _) in _markers)
        {
            double x = XOf(frame);
            var flag = new StreamGeometry();
            using (var g = flag.Open())
            {
                g.BeginFigure(new Point(x, trackTop + 6), true, true);
                g.LineTo(new Point(x - 4, trackTop + 12), true, false);
                g.LineTo(new Point(x + 4, trackTop + 12), true, false);
            }
            dc.DrawGeometry(MarkerBrush, null, flag);
        }

        // Current frame: red line with a red head, as in FlashBack.
        double cx = XOf(_current);
        dc.DrawRectangle(Current, null, new Rect(cx - 1, trackTop - 6, 2, 14));
        dc.DrawEllipse(Current, null, new Point(cx, trackTop + 3), 5, 5);
    }

    private static string FormatTick(double t, double step)
    {
        var ts = TimeSpan.FromSeconds(t);
        if (step < 1) return t.ToString("0.0", CultureInfo.CurrentCulture);
        if (ts.TotalHours >= 1) return ts.ToString(@"h\:mm\:ss");
        if (ts.TotalMinutes >= 1) return ts.ToString(@"m\:ss");
        return $"{t:0}s";
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        long f = FrameAt(e.GetPosition(this).X);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            // Shift+click extends the selection from the current frame (or moves its nearer end).
            if (HasSelection)
            {
                if (Math.Abs(f - _selStart!.Value) < Math.Abs(f - _selEnd!.Value)) SetSelection(f, _selEnd.Value);
                else SetSelection(_selStart.Value, f);
            }
            else SetSelection(_current, f);
            _dragAnchor = -1;
        }
        else
        {
            ClearSelection();
            _dragAnchor = f;
        }
        CaptureMouse();
        Seek?.Invoke(f);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        long f = FrameAt(e.GetPosition(this).X);
        ToolTip = $"Frame {f}  ·  {TimeSpan.FromSeconds(f / _fps):hh\\:mm\\:ss\\.ff}";
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed && _dragAnchor >= 0)
        {
            if (f != _dragAnchor) SetSelection(_dragAnchor, f);
            Seek?.Invoke(f);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        _dragAnchor = -1;
    }
}
