using System.Diagnostics;

namespace ScreenRecorder.Core.Timing;

/// <summary>
/// The single timeline every track is written against. Time stops while paused,
/// so paused stretches never appear in the recording.
/// </summary>
public sealed class RecordingClock
{
    private readonly Func<long> _now;
    private readonly double _ticksPerSecond;
    private readonly object _gate = new();
    private long _start;
    private long _pausedTotal;
    private long _pausedAt;
    private bool _started;
    private bool _paused;

    public RecordingClock() : this(Stopwatch.GetTimestamp, Stopwatch.Frequency) { }

    internal RecordingClock(Func<long> now, double ticksPerSecond)
    {
        _now = now;
        _ticksPerSecond = ticksPerSecond;
    }

    public bool IsStarted { get { lock (_gate) return _started; } }
    public bool IsPaused { get { lock (_gate) return _paused; } }

    public TimeSpan Elapsed
    {
        get
        {
            lock (_gate)
            {
                if (!_started) return TimeSpan.Zero;
                long now = _paused ? _pausedAt : _now();
                return TimeSpan.FromSeconds((now - _start - _pausedTotal) / _ticksPerSecond);
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _start = _now();
            _started = true;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (!_started || _paused) return;
            _pausedAt = _now();
            _paused = true;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_paused) return;
            _pausedTotal += _now() - _pausedAt;
            _paused = false;
        }
    }
}
