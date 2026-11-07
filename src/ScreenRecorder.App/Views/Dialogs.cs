using System.Windows;

namespace ScreenRecorder.App.Views;

/// <summary>Themed message boxes.</summary>
public static class Dialogs
{
    public static void Info(string text, string title = App.ProductName) => MessageWindow.Show(title, text, "", MessageWindow.Buttons.Ok);
    public static void Warn(string text, string title = App.ProductName) => MessageWindow.Show(title, text, "", MessageWindow.Buttons.Ok);
    public static void Error(string text, string title = App.ProductName) => MessageWindow.Show(title, text, "", MessageWindow.Buttons.Ok);
    public static bool Confirm(string text, string title = App.ProductName, string yes = "Yes", string no = "No") =>
        MessageWindow.Show(title, text, "", MessageWindow.Buttons.YesNo, yes, no) == true;

    internal static Window? ActiveOwner() =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible && w.ShowInTaskbar);
}
