using System.Windows;

namespace ScreenRecorder.App.Views;

public partial class MessageWindow : Window
{
    public enum Buttons { Ok, YesNo }

    private MessageWindow() => InitializeComponent();

    public static bool? Show(string title, string text, string icon, Buttons buttons, string yes = "Yes", string no = "No")
    {
        var w = new MessageWindow();
        w.Bar.Title = title;
        w.Title = title;
        w.BodyText.Text = text;
        w.IconText.Text = icon;
        if (buttons == Buttons.Ok)
        {
            w.NoButton.Visibility = Visibility.Collapsed;
            w.YesButton.IsCancel = true;
        }
        else
        {
            w.YesButton.Content = yes;
            w.NoButton.Content = no;
        }
        var owner = Dialogs.ActiveOwner();
        if (owner != null && owner != w) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        return w.ShowDialog();
    }

    private void Yes_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    private void No_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
