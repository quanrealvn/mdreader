using MdReader.Core.Cli;
using MdReader.Core.Diagnostics;
using MdReader.Core.Hosting;

namespace MdReader.Shell.Services;

/// <summary>
/// Last-chance exception policy (§4.11), shared by every shell; the shell only wires its own framework events to it:
/// - UI-thread exception → log + friendly dialog, and the shell marks it handled (test mode: log + Shutdown(1)).
/// - unobserved task exception → log (the shell calls SetObserved).
/// - terminating process-wide exception → log (+ best-effort dialog outside test mode), then exit code 1.
/// </summary>
public sealed class CrashReporter
{
    public const int ExitCodeCrash = 1;
    private const string Category = "Crash";

    private readonly CommandLineOptions _options;
    private readonly AppPaths _paths;
    private readonly IAppHost _host;
    private readonly IAppLog _log;
    private int _dialogOpen;

    public CrashReporter(CommandLineOptions options, AppPaths paths, IAppHost host, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(log);
        _options = options;
        _paths = paths;
        _host = host;
        _log = log;
    }

    /// Exceptions thrown before the UI loop runs (startup failures).
    public void ReportFatal(Exception exception)
    {
        _log.Write(AppLogLevel.Error, Category, "Fatal error during startup", exception);
        ShowDialog("MdReader couldn't start", exception);
    }

    /// An exception reached the UI thread's message loop. The shell marks its event handled afterwards.
    public void ReportUiThreadException(Exception exception)
    {
        _log.Write(AppLogLevel.Error, Category, "Unhandled exception on the UI thread", exception);
        if (_options.IsTestMode)
        {
            _host.Shutdown(ExitCodeCrash);
            return;
        }

        ShowDialog("MdReader ran into a problem", exception);
    }

    public void ReportUnobservedTaskException(Exception? exception) =>
        _log.Write(AppLogLevel.Error, Category, "Unobserved task exception", exception);

    /// A process-wide unhandled exception. Exits the process itself when it is terminating.
    public void ReportProcessException(Exception? exception, bool isTerminating)
    {
        _log.Write(AppLogLevel.Error, Category, $"Unhandled exception (terminating: {isTerminating})", exception);
        if (!isTerminating)
        {
            return;
        }

        if (!_options.IsTestMode)
        {
            try
            {
                ShowDialog("MdReader has to close", exception);
            }
            catch (Exception)
            {
                // Best effort only: the process is going down anyway.
            }
        }

        Environment.Exit(ExitCodeCrash);
    }

    private void ShowDialog(string title, Exception? exception)
    {
        if (_options.IsTestMode || Interlocked.Exchange(ref _dialogOpen, 1) != 0)
        {
            return;   // never stack dialogs when failures repeat
        }

        try
        {
            var message = "Something went wrong. "
                          + (exception is null ? "" : $"{exception.GetType().Name}: {exception.Message}\n\n")
                          + $"Details were saved to {_paths.LogsFolder}";
            _host.ShowError(title, message);
        }
        finally
        {
            Interlocked.Exchange(ref _dialogOpen, 0);
        }
    }
}
