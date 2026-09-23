using Avalonia.Threading;
using MdReader.Shell.Services;

namespace MdReader.Ui.Services;

/// The Avalonia wiring for <see cref="CrashReporter"/> (§4.11): the framework events that can deliver a last-chance
/// exception.
public sealed class CrashHandler
{
    private readonly CrashReporter _reporter;

    public CrashHandler(CrashReporter reporter)
    {
        ArgumentNullException.ThrowIfNull(reporter);
        _reporter = reporter;
    }

    /// <summary>
    /// Call once, on the UI thread, after Avalonia is initialized. Avalonia's dispatcher raises
    /// <c>UnhandledException</c> for anything thrown by a posted callback or an input handler; marking it handled keeps
    /// the message loop alive, the way WPF's <c>DispatcherUnhandledException</c> does.
    /// </summary>
    public void Install()
    {
        Dispatcher.UIThread.UnhandledException += OnDispatcherUnhandledException;
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
