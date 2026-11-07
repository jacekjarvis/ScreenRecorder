using ScreenRecorder.Core.Interop;
using ScreenRecorder.Core.Timing;

namespace ScreenRecorder.Core.Capture;

/// <summary>
/// Calls <c>onFrame(frameIndex)</c> exactly once per frame interval of the recording clock,
/// so frame N always lands at N / fps. If the work falls behind, it catches up by producing
/// frames back to back (they repeat the latest image) rather than dropping time.
/// </summary>
public sealed class FramePacer : IDisposable
{
    private readonly RecordingClock _clock;
    private readonly double _fps;
    private readonly Action<long> _onFrame;
    private readonly Thread _thread;
    private volatile bool _stop;
    private long _frame;

    public long FramesProduced => Interlocked.Read(ref _frame);
    public Exception? Error { get; private set; }
    public event Action<Exception>? Failed;

    public FramePacer(RecordingClock clock, double fps, Action<long> onFrame, string name)
    {
        _clock = clock;
        _fps = fps;
        _onFrame = onFrame;
        _thread = new Thread(Run) { IsBackground = true, Name = name, Priority = ThreadPriority.AboveNormal };
    }

    public void Start() => _thread.Start();

    private void Run()
    {
        Native.timeBeginPeriod(1);
        try
        {
            while (!_stop)
            {
                if (_clock.IsPaused) { Thread.Sleep(5); continue; }
                double due = _frame / _fps;
                double now = _clock.Elapsed.TotalSeconds;
                double wait = due - now;
                if (wait > 0.002) { Thread.Sleep(Math.Max(1, (int)(wait * 1000) - 1)); continue; }
                if (wait > 0) { Thread.SpinWait(50); continue; }
                _onFrame(_frame);
                Interlocked.Increment(ref _frame);
            }
        }
        catch (Exception ex)
        {
            Error = ex;
            Failed?.Invoke(ex);
        }
        finally
        {
            Native.timeEndPeriod(1);
        }
    }

    /// <summary>Stops after producing every frame up to the current clock time.</summary>
    public void Stop()
    {
        _stop = true;
        if (_thread.IsAlive) _thread.Join();
    }

    public void Dispose() => Stop();
}
