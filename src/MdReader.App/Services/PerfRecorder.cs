using System.Diagnostics;
using System.IO;
using System.Text.Json;
using MdReader.Core.Cli;
using MdReader.Core.Diagnostics;

namespace MdReader.App.Services;

/// --perf-log recorder (§4.7, §5). No-op unless --perf-log was given. Marks are milliseconds since process start; the report
/// is written once, 500 ms after the first document reports rendered/enhanced (or at exit if that never happened).
public sealed class PerfRecorder : IPerfRecorder
{
    internal const int ReportSchema = 1;
    internal static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(500);
    private const string Category = "Perf";

    private readonly object _gate = new();
    private readonly Dictionary<string, double> _marks = new(StringComparer.Ordinal);
    private readonly string? _reportPath;
    private readonly UiStallMonitor _stallMonitor;
    private readonly IAppLog _log;
    private readonly DateTime _processStartUtc;
    private readonly double _mainEnteredMs;
    private PerfDocumentInfo? _document;
    private int _reportScheduled;
    private int _reportWritten;

    public PerfRecorder(CommandLineOptions options, UiStallMonitor stallMonitor, IAppLog log)
    {
        _reportPath = options.PerfLogPath;
        _stallMonitor = stallMonitor;
        _log = log;
        if (!IsEnabled)
        {
            return;
        }

        using var process = Process.GetCurrentProcess();
        _processStartUtc = process.StartTime.ToUniversalTime();
        _mainEnteredMs = (StartupClock.MainEnteredUtc - _processStartUtc).TotalMilliseconds;
        _marks[PerfMarks.MainEntered] = _mainEnteredMs;
    }

    public bool IsEnabled => _reportPath is not null;

    public void Mark(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!IsEnabled)
        {
            return;
        }

        var now = _mainEnteredMs + StartupClock.ElapsedSinceMain.TotalMilliseconds;
        lock (_gate)
        {
            _marks.TryAdd(name, now);   // first call per name wins
        }

        if (name == PerfMarks.FirstRenderedEnhanced && Interlocked.Exchange(ref _reportScheduled, 1) == 0)
        {
            _ = Task.Delay(SettleDelay).ContinueWith(_ => WriteReport(), TaskScheduler.Default);
        }
    }

    public void SetDocument(PerfDocumentInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!IsEnabled)
        {
            return;
        }

        lock (_gate)
        {
            _document ??= info;   // first document only
        }
    }

    /// Called at exit: writes the report if the settle timer never did (e.g. the document failed to render).
    internal void WriteReportIfPending() => WriteReport();

    private void WriteReport()
    {
        if (!IsEnabled || Interlocked.Exchange(ref _reportWritten, 1) != 0)
        {
            return;
        }

        PerfReport report;
        lock (_gate)
        {
            report = new PerfReport(ReportSchema, _processStartUtc, new Dictionary<string, double>(_marks, StringComparer.Ordinal),
                                    _stallMonitor.MaxStallMs, _stallMonitor.StallsOver100Ms, _document);
        }

        var path = _reportPath!;
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(report, PerfJsonContext.Default.PerfReport);
            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
            _log.Write(AppLogLevel.Info, Category,
                $"Perf report written to {path} (max UI stall {report.MaxUiStallMs:F1} ms, {report.UiStallsOver100Ms} over 100 ms)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.Write(AppLogLevel.Error, Category, $"Couldn't write the perf report to {path}", ex);
        }
    }
}

/// Timestamps taken at the very top of Program.Main (before anything else runs).
internal static class StartupClock
{
    private static long s_mainEnteredTimestamp;

    internal static DateTime MainEnteredUtc { get; private set; }

    internal static TimeSpan ElapsedSinceMain => Stopwatch.GetElapsedTime(s_mainEnteredTimestamp);

    internal static void MarkMainEntered()
    {
        s_mainEnteredTimestamp = Stopwatch.GetTimestamp();
        MainEnteredUtc = DateTime.UtcNow;
    }
}
