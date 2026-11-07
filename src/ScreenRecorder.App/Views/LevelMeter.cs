using System.Windows;
using System.Windows.Media;

namespace ScreenRecorder.App.Views;

/// <summary>
/// A segmented sound meter (green → amber → red) with a short peak hold, like a recording-level
/// indicator. Feed it the loudest sample (0..1) roughly 30 times a second.
/// </summary>
public sealed class LevelMeter : FrameworkElement
{
    private const int Segments = 16;
    private double _level;      // shown level, 0..1 on a dB scale
    private double _hold;
    private DateTime _holdSince = DateTime.MinValue;

    private static readonly Brush Off = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3A));
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x3F, 0xC2, 0x5A));
    private static readonly Brush Amber = new SolidColorBrush(Color.FromRgb(0xF0, 0xB0, 0x2A));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xE5, 0x3B, 0x3B));

    public LevelMeter()
    {
        Height = 10;
        Width = 96;
        SnapsToDevicePixels = true;
    }

    /// <summary>Sets the latest peak (linear 0..1). Falls back gradually so speech is easy to follow.</summary>
    public void Push(float peak)
    {
        // -54 dB .. 0 dB mapped onto the bar, so quiet speech still moves it.
        double db = peak <= 0.000001 ? -100 : 20 * Math.Log10(peak);
        double target = Math.Clamp((db + 54) / 54, 0, 1);
        _level = target > _level ? target : Math.Max(target, _level - 0.06);
        if (_level >= _hold || (DateTime.UtcNow - _holdSince).TotalMilliseconds > 900)
        {
            _hold = _level;
            _holdSince = DateTime.UtcNow;
        }
        InvalidateVisual();
    }

    public void Reset()
    {
        _level = _hold = 0;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double gap = 2, w = (ActualWidth - gap * (Segments - 1)) / Segments, h = ActualHeight;
        int lit = (int)Math.Round(_level * Segments);
        int hold = (int)Math.Round(_hold * Segments) - 1;
        for (int i = 0; i < Segments; i++)
        {
            var color = i >= Segments - 2 ? Red : i >= Segments - 5 ? Amber : Green;
            var brush = i < lit || i == hold && hold > 0 ? color : Off;
            dc.DrawRoundedRectangle(brush, null, new Rect(i * (w + gap), 0, w, h), 1.5, 1.5);
        }
    }
}
