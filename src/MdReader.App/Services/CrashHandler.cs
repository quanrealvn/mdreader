using System.Windows;
using System.Windows.Threading;
using MdReader.Shell.Services;

namespace MdReader.App.Services;

/// The WPF wiring for <see cref="CrashReporter"/> (§4.11): the framework events that can deliver a last-chance exception.
public sealed class CrashHandler
{
    private readonly CrashReporter _reporter;

    public CrashHandler(CrashReporter reporter)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        _reporter = reporter;
    }

    public void Install(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    /// Used by Program for exceptions thrown before the dispatcher loop runs (startup failures).
    internal void ReportFatal(Exception exception) => _reporter.ReportFatal(exception);

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        _reporter.ReportUiThreadException(e.Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _reporter.ReportUnobservedTaskException(e.Exception);
        e.SetObserved();
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e) =>
        _reporter.ReportProcessException(e.ExceptionObject as Exception, e.IsTerminating);
}
