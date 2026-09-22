namespace MdReader.Core.Threading;

/// <summary>
/// Trailing-edge debouncer: <see cref="Signal"/> (re)starts the delay, and the callback runs once the delay has
/// elapsed without another signal.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>The callback runs on a timer (thread-pool) thread, or synchronously on the caller of <see cref="Flush"/>.
/// It never runs concurrently with itself.</item>
/// <item><see cref="Flush"/> and <see cref="Dispose"/> wait for a callback that is already running on the timer
/// thread. Calling them from inside the callback is fine (the internal lock is re-entrant), but the callback must not
/// block on a thread that may be calling them.</item>
/// <item><see cref="Dispose"/> drops a pending callback (call <see cref="Flush"/> first to run it). After
/// <see cref="Dispose"/> returns the callback never runs again, and further calls are no-ops.</item>
/// <item>Exceptions thrown by the callback propagate: to the caller of <see cref="Flush"/>, or as an unhandled
/// timer-thread exception. The callback is expected to handle its own errors.</item>
/// </list>
/// </remarks>
public sealed class Debouncer : IDisposable
{
    private readonly TimeSpan _delay;
    private readonly Action _callback;
    private readonly TimeProvider _timeProvider;

    // Lock order: _runGate before _gate. _gate guards the fields below; it is never held while the callback runs or
    // while a timer is created or disposed. _runGate serializes callback execution (timer thread vs. Flush).
    private readonly Lock _gate = new();
    private readonly Lock _runGate = new();

    private ITimer? _timer;
    private long _generation;   // bumped by every Signal/Cancel/claim/Dispose; callbacks of stale timers are ignored
    private bool _pending;
    private bool _disposed;

    public Debouncer(TimeSpan delay, Action callback, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);

        _delay = delay;
        _callback = callback;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>(Re)starts the delay. No-op after <see cref="Dispose"/>.</summary>
    public void Signal()
    {
        ITimer? previous;
        long generation;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _pending = true;
            generation = ++_generation;
            previous = _timer;
            _timer = null;
        }

        previous?.Dispose();

        // Created outside the lock: a TimeProvider may invoke a zero-delay callback synchronously.
        var timer = _timeProvider.CreateTimer(
            static state =>
            {
                var timerState = (TimerState)state!;
                timerState.Owner.OnTimer(timerState.Generation);
            },
            new TimerState(this, generation),
            _delay,
            Timeout.InfiniteTimeSpan);

        lock (_gate)
        {
            if (!_disposed && _pending && _generation == generation && _timer is null)
            {
                _timer = timer;
                return;
            }
        }

        // Superseded by a concurrent Signal/Cancel/Flush/Dispose, or it already fired: nothing to keep.
        timer.Dispose();
    }

    /// <summary>Drops a pending callback. Does not wait for a callback that is already running.</summary>
    public void Cancel()
    {
        ITimer? timer;
        lock (_gate)
        {
            _pending = false;
            _generation++;
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
    }

    /// <summary>
    /// If a callback is pending, runs it now, synchronously on the caller. A callback that is already running on the
    /// timer thread is waited for, so its work is complete when this method returns.
    /// </summary>
    public void Flush()
    {
        lock (_runGate)
        {
            if (TryClaimPending(expectedGeneration: null))
            {
                _callback();
            }
        }
    }

    public void Dispose()
    {
        ITimer? timer;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pending = false;
            _generation++;
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();

        // Barrier: wait for a callback that is already running (re-entrant when called from the callback itself).
        lock (_runGate)
        {
        }
    }

    private void OnTimer(long generation)
    {
        lock (_runGate)
        {
            if (TryClaimPending(generation))
            {
                _callback();
            }
        }
    }

    /// <summary>Takes ownership of the pending callback. The caller must hold <see cref="_runGate"/>.</summary>
    private bool TryClaimPending(long? expectedGeneration)
    {
        ITimer? timer;
        lock (_gate)
        {
            if (_disposed || !_pending || (expectedGeneration is { } expected && expected != _generation))
            {
                return false;
            }

            _pending = false;
            _generation++;
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
        return true;
    }

    private sealed class TimerState(Debouncer owner, long generation)
    {
        public Debouncer Owner { get; } = owner;
        public long Generation { get; } = generation;
    }
}
