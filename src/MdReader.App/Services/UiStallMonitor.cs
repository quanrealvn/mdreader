using System.Diagnostics;
using System.Windows.Threading;

namespace MdReader.App.Services;

/// Measures UI-thread responsiveness for --perf-log (§5): a background thread posts a no-op at Normal priority every 16 ms
/// and records the post→run latency. Only started when --perf-log is given.
public sealed class UiStallMonitor : IDisposable
{
    internal static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(16);
    internal const double StallThresholdMs = 100;

    private readonly object _gate = new();
    private Thread? _thread;
    private Dispatcher? _dispatcher;
    private volatile bool _stopping;
    private double _maxStallMs;
    private int _stallsOverThreshold;

    public bool IsRunning => _thread is not null;

    /// Largest post→run latency observed so far, in milliseconds.
    public double MaxStallMs
    {
        get
        {
            lock (_gate)
            {
                return _maxStallMs;
            }
        }
    }

    /// Number of probes whose latency exceeded 100 ms.
    public int StallsOver100Ms
    {
        get
        {
            lock (_gate)
            {
                return _stallsOverThreshold;
            }
        }
    }

    public void Start(Dispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (_thread is not null)
        {
            return;
        }

        _dispatcher = dispatcher;
        _thread = new Thread(Run) { IsBackground = true, Name = "MdReader UI stall monitor", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Dispose()
    {
        _stopping = true;
    }

    private void Run()
    {
        var dispatcher = _dispatcher!;
        using var ran = new ManualResetEventSlim(false);
        long ranAt = 0;
        Action probe = () =>
        {
            Volatile.Write(ref ranAt, Stopwatch.GetTimestamp());
            ran.Set();
        };

        while (!_stopping && !dispatcher.HasShutdownStarted)
        {
            ran.Reset();
            var postedAt = Stopwatch.GetTimestamp();
            dispatcher.BeginInvoke(DispatcherPriority.Normal, probe);

            while (!ran.Wait(250))
            {
                if (_stopping || dispatcher.HasShutdownStarted)
                {
                    return;
                }
            }

            var latencyMs = Stopwatch.GetElapsedTime(postedAt, Volatile.Read(ref ranAt)).TotalMilliseconds;
            lock (_gate)
            {
                if (latencyMs > _maxStallMs)
                {
                    _maxStallMs = latencyMs;
                }

                if (latencyMs > StallThresholdMs)
                {
                    _stallsOverThreshold++;
                }
            }

            Thread.Sleep(ProbeInterval);
        }
    }
}
