using System.Windows;
using ScreenRecorder.Core.Interop;

namespace ScreenRecorder.App.Views;

public partial class InputDialog : Window
{
    private InputDialog() => InitializeComponent();

    /// <summary>Returns the text entered, or null if cancelled. Kept out of recordings.</summary>
    public static string? Ask(string title, string prompt, string initial = "")
    {
        var d = new InputDialog();
        d.Bar.Title = title;
        d.Title = title;
        d.Prompt.Text = prompt;
        d.Answer.Text = initial;
        d.SourceInitialized += (_, _) => Displays.ExcludeFromCapture(new System.Windows.Interop.WindowInteropHelper(d).Handle);
        d.Loaded += (_, _) => { d.Answer.Focus(); d.Answer.SelectAll(); };
        return d.ShowDialog() == true ? d.Answer.Text.Trim() : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
