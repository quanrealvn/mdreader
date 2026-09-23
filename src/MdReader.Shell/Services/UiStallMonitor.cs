using System.Diagnostics;
using MdReader.Shell.Threading;

namespace MdReader.Shell.Services;

/// Measures UI-thread responsiveness for --perf-log (§5): a background thread posts a no-op at Normal priority every 16 ms
/// and records the post→run latency. Only started when --perf-log is given.
public sealed class UiStallMonitor : IDisposable
{
    internal static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(16);
    internal const double StallThresholdMs = 100;

    private readonly object _gate = new();
    private readonly IUiDispatcher _dispatcher;
    private Thread? _thread;
    private volatile bool _stopping;
    private double _maxStallMs;
    private int _stallsOverThreshold;

    public UiStallMonitor(IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

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

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "MdReader UI stall monitor", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Dispose()
    {
        _stopping = true;
    }

    private void Run()
    {
        var dispatcher = _dispatcher;
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
            dispatcher.Post(probe);

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
