using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenRecorder.Core.Export;
using ScreenRecorder.Core.Playback;
using ScreenRecorder.Core.Project;

namespace ScreenRecorder.App.Views;

/// <summary>
/// The Video Editor (FlashBack's "Player"): play the recording, select frames on the timeline,
/// crop to the selection or delete frames from either end, then export to MP4.
/// Edits are non-destructive and saved with the recording.
/// </summary>
public partial class PlayerWindow : Window
{
    private readonly AppSettings _settings;
    private string? _dir;
    private ProjectManifest? _m;
    private FramePreview? _preview;
    private CursorTrack? _cursor;
    private long _sourceFrames;
    private FrameRange _trim = new(0, 0);
    private readonly Stack<FrameRange> _undo = new(), _redo = new();
    private long _current;              // movie frame (0 = first kept frame)
    private WriteableBitmap? _bitmap;
    private VideoPlayback? _video;
    private AudioPlayback? _audio;
    private long _wantedFrame = -1;
    private bool _fetching;

    private long Length => _trim.Count;
    private double Fps => _m?.Fps ?? 30;
    private bool IsPlaying => _video != null;

    public PlayerWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        Bar.CanMaximize = true;
        TimelineBar.Seek += f => { StopPlayback(); ShowFrame(f); };
        TimelineBar.SelectionChanged += UpdateSelectionBoxes;
        PreviewKeyDown += OnKey;
        Closed += (_, _) => StopPlayback();
        UpdateUi();
    }

    // ---------------- opening ----------------

    public void Open(string dir)
    {
        StopPlayback();
        try
        {
            var preview = new FramePreview(dir);
            if (preview.FrameCount <= 0) throw new InvalidDataException("This recording has no frames.");
            _dir = dir;
            _preview = preview;
            _m = preview.Manifest;
            _cursor = CursorTrack.Load(dir);
            _sourceFrames = preview.FrameCount;
            _trim = _m.Trim is { } t && t.Start >= 0 && t.End < _sourceFrames && t.End >= t.Start ? t : new FrameRange(0, _sourceFrames - 1);
            _undo.Clear();
            _redo.Clear();
            _current = 0;
            _bitmap = null;
            Bar.Title = $"ScreenRecorder Video Editor — {Path.GetFileName(dir)}";
            EmptyHint.Visibility = Visibility.Collapsed;
            _settings.AddRecent(dir);
            RefreshTimeline();
            ShowFrame(0);
            StatusMessage.Text = _m.Completed ? "" : "This recording was not finished cleanly; everything that was saved can still be used.";
        }
        catch (Exception ex)
        {
            Dialogs.Error($"This recording can't be opened.\n\n{ex.Message}");
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open recording",
            Filter = "ScreenRecorder recording|project.json",
            InitialDirectory = Directory.Exists(_settings.OutputFolder) ? _settings.OutputFolder : null,
        };
        if (dlg.ShowDialog(this) == true) Open(Path.GetDirectoryName(dlg.FileName)!);
    }

    private void Movie_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            string p = paths[0];
            string dir = Directory.Exists(p) ? p : Path.GetDirectoryName(p)!;
            if (File.Exists(Path.Combine(dir, ProjectManifest.FileName))) Open(dir);
        }
    }

    // ---------------- showing frames ----------------

    private (int W, int H) PreviewSize()
    {
        if (_m == null) return (2, 2);
        var dpi = VisualTreeHelper.GetDpi(this);
        double maxW = Math.Max(160, (MovieArea.ActualWidth - 16) * dpi.DpiScaleX);
        double maxH = Math.Max(90, (MovieArea.ActualHeight - 16) * dpi.DpiScaleY);
        double scale = Math.Min(1.0, Math.Min(maxW / _m.Width, maxH / _m.Height));
        return (Math.Max(2, (int)(_m.Width * scale) & ~1), Math.Max(2, (int)(_m.Height * scale) & ~1));
    }

    private void EnsureBitmap(int w, int h)
    {
        if (_bitmap != null && _bitmap.PixelWidth == w && _bitmap.PixelHeight == h) return;
        _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
        MovieImage.Source = _bitmap;
    }

    /// <summary>Jumps to a movie frame. Rapid requests (scrubbing) collapse to the latest one.</summary>
    private async void ShowFrame(long frame)
    {
        if (_preview == null) return;
        _current = Math.Clamp(frame, 0, Length - 1);
        UpdatePosition();
        _wantedFrame = _current;
        if (_fetching) return;
        _fetching = true;
        try
        {
            while (_wantedFrame >= 0)
            {
                long f = _wantedFrame;
                _wantedFrame = -1;
                var (w, h) = PreviewSize();
                var img = await _preview.GetAsync(_trim.Start + f, w, h);
                if (img == null || IsPlaying) continue;
                EnsureBitmap(img.Width, img.Height);
                _bitmap!.WritePixels(new Int32Rect(0, 0, img.Width, img.Height), img.Bgra, img.Width * 4, 0);
            }
        }
        catch (Exception ex) { StatusMessage.Text = $"Couldn't show that frame: {ex.Message}"; }
        finally { _fetching = false; }
    }

    // ---------------- playback ----------------

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (_m == null || _dir == null || _cursor == null) return;
        if (IsPlaying) { StopPlayback(); ShowFrame(_current); return; }
        if (_current >= Length - 1) _current = 0;
        var (w, h) = PreviewSize();
        EnsureBitmap(w, h);
        long firstSource = _trim.Start + _current;
        var video = new VideoPlayback(_dir, _m, _cursor, w, h, firstSource, _trim.End, (frame, buf) =>
            Dispatcher.Invoke(() =>
            {
                if (_video == null || _bitmap == null || _bitmap.PixelWidth != w) return;
                _bitmap.WritePixels(new Int32Rect(0, 0, w, h), buf, w * 4, 0);
                _current = frame - _trim.Start;
                UpdatePosition();
            }));
        video.Ended += () => Dispatcher.BeginInvoke(() => { StopPlayback(); UpdateUi(); });
        _video = video;
        try
        {
            _audio = new AudioPlayback(_dir, _m, firstSource / Fps, (_trim.End - firstSource + 1) / Fps);
            video.Start();
            _audio.Play();
        }
        catch (Exception ex)
        {
            StopPlayback();
            Dialogs.Error($"Playback failed: {ex.Message}");
        }
        UpdateUi();
    }

    private void StopPlayback()
    {
        var v = _video;
        _video = null;
        v?.Dispose();
        _audio?.Dispose();
        _audio = null;
        PlayButton.Content = "\uE768";
    }

    // ---------------- selection & edits ----------------

    private (long Start, long End)? Selection =>
        TimelineBar.HasSelection ? (TimelineBar.SelectionStart!.Value, TimelineBar.SelectionEnd!.Value) : null;

    private void SetStart_Click(object sender, RoutedEventArgs e)
    {
        if (_m == null) return;
        long end = Selection?.End ?? Length - 1;
        TimelineBar.SetSelection(_current, Math.Max(end, _current));
    }

    private void SetEnd_Click(object sender, RoutedEventArgs e)
    {
        if (_m == null) return;
        long start = Selection?.Start ?? 0;
        TimelineBar.SetSelection(Math.Min(start, _current), _current);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) { if (_m != null) TimelineBar.SetSelection(0, Length - 1); }
    private void SelectNone_Click(object sender, RoutedEventArgs e) => TimelineBar.ClearSelection();

    private void Crop_Click(object sender, RoutedEventArgs e)
    {
        if (Selection is not { } s) { Dialogs.Info("Select the frames to keep first: drag on the timeline, Shift+click, or use [ Start and End ]."); return; }
        if (s.Start == 0 && s.End == Length - 1) return;
        ApplyEdit(new FrameRange(_trim.Start + s.Start, _trim.Start + s.End), $"Cropped to frames {s.Start}–{s.End}.");
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selection is not { } s) { Dialogs.Info("Select the frames to delete first: drag on the timeline or Shift+click."); return; }
        if (s.Start == 0 && s.End == Length - 1) { Dialogs.Warn("You can't delete every frame of the movie."); return; }
        if (s.Start == 0)
            ApplyEdit(new FrameRange(_trim.Start + s.End + 1, _trim.End), $"Deleted {s.End + 1} frames from the start.");
        else if (s.End == Length - 1)
            ApplyEdit(new FrameRange(_trim.Start, _trim.Start + s.Start - 1), $"Deleted {s.End - s.Start + 1} frames from the end.");
        else
            Dialogs.Info("In this version, frames can be deleted from the start or the end of the movie.\n\nTo cut a movie down to frames X–Y, select them and use Crop to selection.");
    }

    private void RestoreAll_Click(object sender, RoutedEventArgs e)
    {
        if (_m == null) return;
        ApplyEdit(new FrameRange(0, _sourceFrames - 1), "Restored the full recording.");
    }

    private void ApplyEdit(FrameRange next, string message)
    {
        if (_m == null || next == _trim) return;
        StopPlayback();
        _undo.Push(_trim);
        _redo.Clear();
        SetTrim(next, message);
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0) return;
        _redo.Push(_trim);
        SetTrim(_undo.Pop(), "Undone.");
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (_redo.Count == 0) return;
        _undo.Push(_trim);
        SetTrim(_redo.Pop(), "Redone.");
    }

    private void SetTrim(FrameRange range, string message)
    {
        // Keep the same picture on screen where possible.
        long sourceCurrent = _trim.Start + _current;
        _trim = range;
        if (_m != null && _dir != null)
        {
            _m.Trim = range.Start == 0 && range.End == _sourceFrames - 1 ? null : range;
            try { _m.Save(_dir); } catch (Exception ex) { message += $" (Couldn't save the edit: {ex.Message})"; }
        }
        RefreshTimeline();
        ShowFrame(Math.Clamp(sourceCurrent - range.Start, 0, Length - 1));
        StatusMessage.Text = message;
    }

    private void RefreshTimeline()
    {
        if (_m == null) return;
        var markers = _m.Markers
            .Select(mk => ((long)Math.Round(mk.Time * Fps) - _trim.Start, mk.Label))
            .Where(mk => mk.Item1 >= 0 && mk.Item1 < Length)
            .ToList();
        TimelineBar.SetMovie(Length, Fps, markers);
        MarkersMenu.Items.Clear();
        foreach (var (frame, label) in markers)
        {
            var item = new MenuItem { Header = $"{label}  ({TimeSpan.FromSeconds(frame / Fps):hh\\:mm\\:ss})" };
            item.Click += (_, _) => { StopPlayback(); ShowFrame(frame); };
            MarkersMenu.Items.Add(item);
        }
        MarkersMenu.IsEnabled = markers.Count > 0;
        UpdateUi();
    }

    private void UpdateSelectionBoxes()
    {
        SelStartBox.Text = Selection?.Start.ToString() ?? "";
        SelEndBox.Text = Selection?.End.ToString() ?? "";
        UpdateUi();
    }

    private void SelBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplySelectionBoxes(); e.Handled = true; }
    }

    private void SelBox_LostFocus(object sender, RoutedEventArgs e) => ApplySelectionBoxes();

    private void ApplySelectionBoxes()
    {
        if (_m == null) return;
        bool a = long.TryParse(SelStartBox.Text, out long s), b = long.TryParse(SelEndBox.Text, out long en);
        if (a && b) TimelineBar.SetSelection(s, en);
        else if (a) TimelineBar.SetSelection(s, Length - 1);
        else if (b) TimelineBar.SetSelection(0, en);
        else if (SelStartBox.Text.Length == 0 && SelEndBox.Text.Length == 0) TimelineBar.ClearSelection();
        UpdateSelectionBoxes();
    }

    // ---------------- navigation ----------------

    private void First_Click(object sender, RoutedEventArgs e) { StopPlayback(); ShowFrame(0); }
    private void Last_Click(object sender, RoutedEventArgs e) { StopPlayback(); ShowFrame(Length - 1); }
    private void PrevFrame_Click(object sender, RoutedEventArgs e) => Step(-1);
    private void NextFrame_Click(object sender, RoutedEventArgs e) => Step(1);

    private void Step(long frames)
    {
        if (_m == null) return;
        StopPlayback();
        ShowFrame(_current + frames);
    }

    private void GoTo_Click(object sender, RoutedEventArgs e)
    {
        if (_m == null) return;
        var text = InputDialog.Ask("Go to frame", $"Frame number (0 – {Length - 1}), or a time like 1:23:45:", _current.ToString());
        if (text == null) return;
        if (long.TryParse(text, out long f)) { Step(f - _current); return; }
        var parts = text.Split(':').Select(p => double.TryParse(p, out var v) ? v : double.NaN).ToArray();
        if (parts.Length is >= 2 and <= 3 && parts.All(p => !double.IsNaN(p)))
        {
            double secs = parts.Aggregate(0.0, (acc, p) => acc * 60 + p);
            Step((long)Math.Round(secs * Fps) - _current);
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource is TextBox) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        long second = Math.Max(1, (long)Math.Round(Fps));
        switch (e.Key)
        {
            case Key.Space: TogglePlay(); break;
            case Key.Left: Step(shift ? -second : -1); break;
            case Key.Right: Step(shift ? second : 1); break;
            case Key.Home: First_Click(this, e); break;
            case Key.End: Last_Click(this, e); break;
            case Key.OemOpenBrackets: SetStart_Click(this, e); break;
            case Key.OemCloseBrackets: SetEnd_Click(this, e); break;
            case Key.Delete: Delete_Click(this, e); break;
            case Key.Z when ctrl: Undo_Click(this, e); break;
            case Key.Y when ctrl: Redo_Click(this, e); break;
            case Key.A when ctrl: SelectAll_Click(this, e); break;
            case Key.E when ctrl: Export_Click(this, e); break;
            case Key.O when ctrl: Open_Click(this, e); break;
            case Key.G when ctrl: GoTo_Click(this, e); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---------------- output ----------------

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_dir == null) { Open_Click(sender, e); return; }
        StopPlayback();
        var w = new ExportWindow(_settings, _dir, _trim.Start, _trim.End) { Owner = this };
        w.Show();
    }

    private async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        if (_preview == null || _m == null || _dir == null) return;
        StopPlayback();
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save screenshot",
            Filter = "PNG image|*.png",
            FileName = $"{Path.GetFileName(_dir)} frame {_current}.png",
            InitialDirectory = _settings.LastExportFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dlg.ShowDialog(this) != true) return;
        var img = await _preview.GetAsync(_trim.Start + _current, _m.Width, _m.Height);
        if (img == null) { Dialogs.Error("That frame couldn't be read."); return; }
        try
        {
            Png.WriteBgra(dlg.FileName, img.Width, img.Height, img.Bgra, img.Width * 4);
            StatusMessage.Text = $"Screenshot saved: {dlg.FileName}";
        }
        catch (Exception ex) { Dialogs.Error($"The screenshot couldn't be saved: {ex.Message}"); }
    }

    private void ShowFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_dir != null) Process.Start(new ProcessStartInfo(_dir) { UseShellExecute = true });
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void Help_Click(object sender, RoutedEventArgs e) => Dialogs.Info(
        "To keep only frames X to Y:\n" +
        "  1. Drag across the timeline (or Shift+click two points), or type the frame numbers into \"Selected frames\" at the bottom.\n" +
        "  2. Click Crop to selection.\n\n" +
        "To cut frames off the start or end: select them and press Delete.\n\n" +
        "Then click Export MP4. Undo (Ctrl+Z) reverses any edit; the original recording is never changed.\n\n" +
        "Keys: Space play/pause · ←/→ one frame (Shift: one second) · [ and ] set the selection start/end at the current frame.",
        "How to trim and export");

    // ---------------- status ----------------

    private void UpdatePosition()
    {
        TimelineBar.CurrentFrame = _current;
        FrameText.Text = _m == null ? "" : $"Frame {_current:N0}";
        TimeClock.Text = _m == null ? "" : TimeSpan.FromSeconds(_current / Fps).ToString(@"hh\:mm\:ss\.ff");
    }

    private void UpdateUi()
    {
        bool open = _m != null;
        LengthText.Text = open ? $"Length {TimeSpan.FromSeconds(Length / Fps):hh\\:mm\\:ss\\.ff}" : "";
        LengthClock.Text = open ? TimeSpan.FromSeconds(Length / Fps).ToString(@"hh\:mm\:ss\.ff") : "";
        PlayButton.Content = IsPlaying ? "\uE769" : "\uE768";
        UndoButton.IsEnabled = UndoMenu.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = RedoMenu.IsEnabled = _redo.Count > 0;
        CropButton.IsEnabled = DeleteButton.IsEnabled = open && TimelineBar.HasSelection;
        SelStartBox.IsEnabled = SelEndBox.IsEnabled = open;
        UpdatePosition();
    }
}
