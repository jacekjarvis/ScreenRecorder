using WinForms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace ScreenRecorder.App.Services;

/// <summary>
/// The notification-area icon and its menu (Open main window, Record, Stop, Add marker,
/// Restart recording, Help, Minimize to tray / floating controls, About, Exit) — as in FlashBack.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly RecorderController _c;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly Dictionary<AppIcons.State, Drawing.Icon> _icons = new();
    private AppIcons.State _shown = (AppIcons.State)(-1);

    public TrayIcon(RecorderController controller)
    {
        _c = controller;
        _menu = new WinForms.ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false, Font = new Drawing.Font("Segoe UI", 9.5f) };
        _menu.Opening += (_, _) => BuildMenu();
        _icon = new WinForms.NotifyIcon { Text = "ScreenRecorder", Visible = true, ContextMenuStrip = _menu };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) ShowMenu();
        };
        _icon.MouseDoubleClick += (_, _) => App.Current.RestoreRecorder();
        _c.StateChanged += Update;
        _c.Tick += UpdateText;
        Update();
    }

    /// <summary>Shows the menu at the mouse (used by the tray icon and the floating toolbar's ▾ button).</summary>
    public void ShowMenu()
    {
        BuildMenu();
        _menu.Show(WinForms.Cursor.Position);
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        bool recording = _c.State is RecorderState.Recording or RecorderState.Paused;
        Add("Open main window", () => App.Current.RestoreRecorder(), bold: true);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        Add(_c.State == RecorderState.Scheduled ? "Cancel scheduled recording" : "Record", () => _ = _c.RecordAsync(), enabled: !_c.IsBusy);
        Add("Stop", () => _ = _c.StopAsync(), enabled: recording);
        Add(_c.State == RecorderState.Paused ? "Resume" : "Pause", _c.TogglePause, enabled: recording);
        Add("Add marker", _c.AddMarker, enabled: recording);
        Add("Restart recording", () =>
        {
            if (Views.Dialogs.Confirm("Discard everything recorded so far and start again?", "Restart recording", "Restart", "Cancel"))
                _ = _c.RestartAsync();
        }, enabled: recording);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        Add("Video Editor", () => App.Current.OpenPlayer(_c.Settings.RecentRecordings.FirstOrDefault(System.IO.Directory.Exists)));
        Add("Help", () => App.Current.ShowHelp());
        var tray = Add("Minimize to tray", () => SetMinimize(MinimizeMode.Tray));
        tray.Checked = _c.Settings.MinimizeTo == MinimizeMode.Tray;
        var floating = Add("Minimize to floating controls", () => SetMinimize(MinimizeMode.FloatingControls));
        floating.Checked = _c.Settings.MinimizeTo == MinimizeMode.FloatingControls;
        Add("About", () => App.Current.ShowAbout());
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        Add("Exit", () => _ = App.Current.ExitAsync());
    }

    private void SetMinimize(MinimizeMode mode)
    {
        _c.Settings.MinimizeTo = mode;
        _c.Settings.Save();
        App.Current.ApplyMinimizeMode();
    }

    private WinForms.ToolStripMenuItem Add(string text, Action action, bool enabled = true, bool bold = false)
    {
        var item = new WinForms.ToolStripMenuItem(text) { Enabled = enabled };
        if (bold) item.Font = new Drawing.Font(item.Font, Drawing.FontStyle.Bold);
        item.Click += (_, _) => System.Windows.Application.Current.Dispatcher.BeginInvoke(action);
        _menu.Items.Add(item);
        return item;
    }

    private void Update()
    {
        var state = _c.State switch
        {
            RecorderState.Recording => AppIcons.State.Recording,
            RecorderState.Paused => AppIcons.State.Paused,
            _ => AppIcons.State.Idle,
        };
        if (state != _shown)
        {
            if (!_icons.TryGetValue(state, out var icon)) _icons[state] = icon = AppIcons.Create(state);
            _icon.Icon = icon;
            _shown = state;
        }
        UpdateText();
    }

    private void UpdateText()
    {
        string text = _c.State switch
        {
            RecorderState.Recording => $"ScreenRecorder — recording {_c.Elapsed:hh\\:mm\\:ss}",
            RecorderState.Paused => $"ScreenRecorder — paused at {_c.Elapsed:hh\\:mm\\:ss}",
            RecorderState.Scheduled => "ScreenRecorder — waiting for scheduled start",
            _ => "ScreenRecorder",
        };
        if (_icon.Text != text) _icon.Text = text;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        foreach (var i in _icons.Values) i.Dispose();
    }

    private sealed class DarkRenderer : WinForms.ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Drawing.Color.FromArgb(0xED, 0xED, 0xED) : Drawing.Color.FromArgb(0x80, 0x80, 0x80);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(WinForms.ToolStripItemImageRenderEventArgs e)
        {
            using var b = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x2F, 0x7F, 0xE0));
            var r = new Drawing.Rectangle(e.ImageRectangle.X + 4, e.ImageRectangle.Y + 4, 8, 8);
            e.Graphics.FillEllipse(b, r);
        }
    }

    private sealed class DarkColors : WinForms.ProfessionalColorTable
    {
        private static readonly Drawing.Color Back = Drawing.Color.FromArgb(0x33, 0x33, 0x33);
        private static readonly Drawing.Color Accent = Drawing.Color.FromArgb(0x2F, 0x7F, 0xE0);
        public override Drawing.Color ToolStripDropDownBackground => Back;
        public override Drawing.Color MenuBorder => Drawing.Color.FromArgb(0x55, 0x55, 0x55);
        public override Drawing.Color MenuItemBorder => Accent;
        public override Drawing.Color MenuItemSelected => Accent;
        public override Drawing.Color MenuItemSelectedGradientBegin => Accent;
        public override Drawing.Color MenuItemSelectedGradientEnd => Accent;
        public override Drawing.Color ImageMarginGradientBegin => Back;
        public override Drawing.Color ImageMarginGradientMiddle => Back;
        public override Drawing.Color ImageMarginGradientEnd => Back;
        public override Drawing.Color SeparatorDark => Drawing.Color.FromArgb(0x48, 0x48, 0x48);
        public override Drawing.Color SeparatorLight => Drawing.Color.FromArgb(0x48, 0x48, 0x48);
        public override Drawing.Color CheckBackground => Back;
        public override Drawing.Color CheckSelectedBackground => Accent;
    }
}
