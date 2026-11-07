using System.Globalization;
using System.Windows;

namespace ScreenRecorder.App.Views;

/// <summary>Set Timer: start recording at a time; stop after a duration or at a time.</summary>
public partial class TimerWindow : Window
{
    private readonly AppSettings _s;

    public TimerWindow(AppSettings settings)
    {
        InitializeComponent();
        _s = settings;
        var start = settings.ScheduleStart > DateTime.Now ? settings.ScheduleStart : DateTime.Now.AddMinutes(5);
        var stop = settings.ScheduleStop > start ? settings.ScheduleStop : start.AddHours(1);
        UseScheduling.IsChecked = settings.ScheduleEnabled;
        StartDate.Text = start.ToString("d");
        StartTime.Text = start.ToString("HH:mm");
        StopDate.Text = stop.ToString("d");
        StopTime.Text = stop.ToString("HH:mm");
        Duration.Text = settings.ScheduleDurationMinutes.ToString();
        ByDuration.IsChecked = settings.ScheduleStopByDuration;
        ByTime.IsChecked = !settings.ScheduleStopByDuration;
        Changed(this, new RoutedEventArgs());
    }

    private void Changed(object sender, RoutedEventArgs e)
    {
        Fields.IsEnabled = UseScheduling.IsChecked == true;
        Duration.IsEnabled = ByDuration.IsChecked == true;
        StopDate.IsEnabled = StopTime.IsEnabled = ByTime.IsChecked == true;
    }

    /// <summary>Date in the PC's own format (as shown), time as 24-hour HH:mm.</summary>
    private static DateTime? Combine(string date, string time) =>
        DateTime.TryParse(date.Trim(), CultureInfo.CurrentCulture, DateTimeStyles.None, out var d) &&
        TimeSpan.TryParseExact(time.Trim(), new[] { @"h\:mm", @"hh\:mm" }, CultureInfo.InvariantCulture, out var t)
            ? d.Date + t : null;

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (UseScheduling.IsChecked != true)
        {
            _s.ScheduleEnabled = false;
            _s.Save();
            DialogResult = true;
            return;
        }
        var start = Combine(StartDate.Text, StartTime.Text);
        if (start == null) { ShowError($"Enter the start date as {DateTime.Now:d} and the time as HH:mm, e.g. 14:30."); return; }
        if (start <= DateTime.Now) { ShowError("The start time has already passed."); return; }
        bool byDuration = ByDuration.IsChecked == true;
        int minutes = 0;
        DateTime? stop = null;
        if (byDuration && (!int.TryParse(Duration.Text, out minutes) || minutes <= 0)) { ShowError("Enter the duration in whole minutes."); return; }
        if (!byDuration)
        {
            stop = Combine(StopDate.Text, StopTime.Text);
            if (stop == null || stop <= start) { ShowError("The stop time must be after the start time."); return; }
        }
        _s.ScheduleEnabled = true;
        _s.ScheduleStart = start.Value;
        _s.ScheduleStopByDuration = byDuration;
        if (byDuration) _s.ScheduleDurationMinutes = minutes; else _s.ScheduleStop = stop!.Value;
        _s.Save();
        DialogResult = true;
    }

    private void ShowError(string text)
    {
        Error.Text = text;
        Error.Visibility = Visibility.Visible;
    }
}
