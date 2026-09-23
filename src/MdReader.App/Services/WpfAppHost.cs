using System.Windows;
using MdReader.Core.Cli;
using MdReader.Shell.Services;

namespace MdReader.App.Services;

/// <see cref="IAppHost"/> for the WPF shell. Dialogs are owned by the main window once it is visible and are skipped
/// entirely in test mode (§4.7).
public sealed class WpfAppHost : IAppHost
{
    private readonly CommandLineOptions _options;

    public WpfAppHost(CommandLineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public void Shutdown(int exitCode) => Application.Current?.Shutdown(exitCode);

    public void ShowError(string title, string message)
    {
        if (_options.IsTestMode)
        {
            return;
        }

        // GetOwner touches Application.MainWindow, so it may only be read from the UI thread (a crash reported from a
        // background thread has no owner).
        var owner = Application.Current?.Dispatcher.CheckAccess() == true ? DialogService.GetOwner() : null;
        if (owner is not null)
        {
            MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        else
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public bool ConfirmError(string title, string message)
    {
        if (_options.IsTestMode)
        {
            return false;
        }

        var owner = DialogService.GetOwner();
        var answer = owner is not null
            ? MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, MessageBoxImage.Error)
            : MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Error);
        return answer == MessageBoxResult.Yes;
    }

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }
}
