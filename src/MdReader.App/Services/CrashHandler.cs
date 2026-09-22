using System.Windows;
using System.Windows.Threading;
using MdReader.Core.Cli;
using MdReader.Core.Diagnostics;
using MdReader.Core.Hosting;

namespace MdReader.App.Services;

/// Last-chance exception handling (§4.11):
/// - Dispatcher.UnhandledException → log + friendly dialog + Handled (test mode: log + Shutdown(1)).
/// - TaskScheduler.UnobservedTaskException → log + SetObserved.
/// - AppDomain.UnhandledException → log (+ best-effort dialog outside test mode), then exit code 1.
public sealed class CrashHandler
{
    internal const int ExitCodeCrash = 1;
    private const string Category = "Crash";

    private readonly CommandLineOptions _options;
    private readonly AppPaths _paths;
    private readonly IAppLog _log;
    private int _dialogOpen;

    public CrashHandler(CommandLineOptions options, AppPaths paths, IAppLog log)
    {
        _options = options;
        _paths = paths;
        _log = log;
    }

    public void Install(Application application)
    {
        ArgumentNullException.ThrowIfNull(application);
        application.DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    /// Used by Program for exceptions thrown before the dispatcher loop runs (startup failures).
    internal void ReportFatal(Exception exception)
    {
        _log.Write(AppLogLevel.Error, Category, "Fatal error during startup", exception);
        ShowDialog("MdReader couldn't start", exception);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _log.Write(AppLogLevel.Error, Category, "Unhandled exception on the UI thread", e.Exception);
        e.Handled = true;
        if (_options.IsTestMode)
        {
            Application.Current?.Shutdown(ExitCodeCrash);
            return;
        }

        ShowDialog("MdReader ran into a problem", e.Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _log.Write(AppLogLevel.Error, Category, "Unobserved task exception", e.Exception);
        e.SetObserved();
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exception = e.ExceptionObject as Exception;
        _log.Write(AppLogLevel.Error, Category, $"Unhandled exception (terminating: {e.IsTerminating})", exception);
        if (!e.IsTerminating)
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
            var onUiThread = Application.Current?.Dispatcher.CheckAccess() == true;
            var owner = onUiThread ? DialogService.GetOwner() : null;
            if (owner is not null)
            {
                MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _dialogOpen, 0);
        }
    }
}
