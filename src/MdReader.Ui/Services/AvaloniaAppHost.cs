using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MdReader.Core.Cli;
using MdReader.Shell.Services;

namespace MdReader.Ui.Services;

/// <see cref="IAppHost"/> for the Avalonia shell. Dialogs are owned by the main window once it is visible and are
/// skipped entirely in test mode (§4.7).
public sealed partial class AvaloniaAppHost : IAppHost
{
    private readonly CommandLineOptions _options;

    public AvaloniaAppHost(CommandLineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Deferred on purpose: the shutdown is usually requested from inside a window or WebView callback, and tearing the
    /// desktop lifetime down there pulls the visual tree apart underneath Avalonia.
    /// </summary>
    public void Shutdown(int exitCode) => Dispatcher.UIThread.Post(() =>
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            lifetime.Shutdown(exitCode);
        }
        else
        {
            Environment.Exit(exitCode);
        }
    });

    public void ShowError(string title, string message)
    {
        if (!_options.IsTestMode)
        {
            ShowErrorCore(title, message);
        }
    }

    public bool ConfirmError(string title, string message) => !_options.IsTestMode && ConfirmErrorCore(title, message);

    /// The platform's own error alert, owned by the main window where the platform has a notion of an owner.
    private static partial void ShowErrorCore(string title, string message);

    /// The same with Yes and No; false when the user said no.
    private static partial bool ConfirmErrorCore(string title, string message);

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        Dispatcher.UIThread.Post(action);
    }

    /// The main window once it is visible; dialogs are then modal to it and centered on it. Zero off the UI thread (a
    /// crash reported from a background thread has no owner).
    internal static nint GetOwnerHandle()
    {
        if (!Dispatcher.UIThread.CheckAccess()
            || Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime lifetime)
        {
            return 0;
        }

        Window? window = lifetime.MainWindow;
        return window is { IsVisible: true } ? window.TryGetPlatformHandle()?.Handle ?? 0 : 0;
    }
}
