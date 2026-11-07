using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using ScreenRecorder.Core.Interop;

namespace ScreenRecorder.App.Views;

/// <summary>Record-time note: appears when the mouse is pushed against the chosen screen edge.</summary>
public partial class NoteWindow : Window
{
    private readonly bool _saveOnLeave;
    private bool _hadMouse;

    public double TimeSeconds { get; }
    public string? SavedText { get; private set; }

    public NoteWindow(double timeSeconds, bool saveOnLeave)
    {
        InitializeComponent();
        TimeSeconds = timeSeconds;
        _saveOnLeave = saveOnLeave;
        Header.Text = $"Note at {TimeSpan.FromSeconds(timeSeconds):hh\\:mm\\:ss}";
        SourceInitialized += (_, _) => Displays.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) => { Activate(); NoteText.Focus(); };
        MouseEnter += (_, _) => _hadMouse = true;
        MouseLeave += (_, _) => { if (_saveOnLeave && _hadMouse) SaveAndClose(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    /// <summary>Places the window against the edge the mouse was pushed into, under the cursor.</summary>
    public void ShowNear(PixelRect screen, int cursorX, int cursorY, ScreenEdge edge)
    {
        Show();
        var source = PresentationSource.FromVisual(this);
        double sx = source?.CompositionTarget?.TransformToDevice.M11 ?? 1, sy = source?.CompositionTarget?.TransformToDevice.M22 ?? 1;
        double w = ActualWidth * sx, h = ActualHeight * sy;
        double x = Math.Clamp(cursorX - w / 2, screen.X, screen.Right - w);
        double y = Math.Clamp(cursorY - h / 2, screen.Y, screen.Bottom - h);
        switch (edge)
        {
            case ScreenEdge.Top: y = screen.Y; break;
            case ScreenEdge.Bottom: y = screen.Bottom - h; break;
            case ScreenEdge.Left: x = screen.X; break;
            case ScreenEdge.Right: x = screen.Right - w; break;
        }
        Left = x / sx;
        Top = y / sy;
        Activate();
    }

    public void SaveAndClose()
    {
        SavedText = NoteText.Text.Trim();
        Close();
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveAndClose();
}
