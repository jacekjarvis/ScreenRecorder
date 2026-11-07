using ScreenRecorder.Core.Interop;

namespace ScreenRecorder.Core.Capture;

/// <summary>What to record: a rectangle of the desktop (one monitor, several, or a region) or a single window.</summary>
public abstract record CaptureTarget
{
    public abstract string Mode { get; }
    public abstract string Description { get; }

    /// <summary>A fixed area of the virtual desktop in physical pixels. May span monitors.</summary>
    public sealed record Area(PixelRect Bounds, string Label) : CaptureTarget
    {
        public override string Mode => "area";
        public override string Description => $"{Label} — {Bounds}";
    }

    /// <summary>
    /// A window, captured through its own content (stays correct when covered by other windows).
    /// The movie keeps the window's starting size.
    /// </summary>
    public sealed record Window(IntPtr Handle, string Title) : CaptureTarget
    {
        public override string Mode => "window";
        public override string Description => $"Window — {Title}";
    }

    public static CaptureTarget FullScreen(MonitorInfo m) => new Area(m.Bounds, m.DisplayName);
    public static CaptureTarget AllMonitors() => new Area(Displays.VirtualScreen, "All monitors");
    public static CaptureTarget Region(PixelRect r) => new Area(r, "Region");
}
