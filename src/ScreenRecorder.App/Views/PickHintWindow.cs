using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ScreenRecorder.App.Views;

/// <summary>A small banner at the top of the screen with an instruction ("Click the window…").</summary>
public sealed class PickHintWindow : Window
{
    public PickHintWindow(string text)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = new Border
        {
            Background = (Brush)Application.Current.Resources["AccentBrush"],
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(18, 10, 18, 10),
            Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 15, FontFamily = new FontFamily("Segoe UI") },
        };
        Loaded += (_, _) =>
        {
            Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - ActualWidth) / 2;
            Top = SystemParameters.WorkArea.Top + 24;
        };
        SourceInitialized += (_, _) => Core.Interop.Displays.ExcludeFromCapture(new System.Windows.Interop.WindowInteropHelper(this).Handle);
    }
}
