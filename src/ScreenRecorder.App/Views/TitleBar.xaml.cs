using System.Windows;
using System.Windows.Controls;

namespace ScreenRecorder.App.Views;

public partial class TitleBar : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(nameof(Title), typeof(string), typeof(TitleBar),
        new PropertyMetadata("", (d, e) => ((TitleBar)d).TitleText.Text = (string)e.NewValue));

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public bool CanMinimize { get => MinButton.Visibility == Visibility.Visible; set => MinButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }
    public bool CanMaximize { get => MaxButton.Visibility == Visibility.Visible; set => MaxButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed; }

    /// <summary>Raised instead of minimizing, so the owner can minimize to the tray or floating controls.</summary>
    public event EventHandler? MinimizeRequested;

    public TitleBar()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var w = Window.GetWindow(this);
            if (w == null) return;
            if (w.ResizeMode is ResizeMode.NoResize or ResizeMode.CanMinimize) CanMaximize = false;
            w.StateChanged += (_, _) => MaxButton.Content = w.WindowState == WindowState.Maximized ? "" : "";
        };
    }

    private void Min_Click(object sender, RoutedEventArgs e)
    {
        if (MinimizeRequested != null) MinimizeRequested(this, EventArgs.Empty);
        else if (Window.GetWindow(this) is { } w) w.WindowState = WindowState.Minimized;
    }

    private void Max_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } w) w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Window.GetWindow(this)?.Close();
}
