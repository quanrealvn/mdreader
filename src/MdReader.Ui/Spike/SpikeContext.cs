using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MdReader.Core.Cli;
using MdReader.Core.Diagnostics;
using MdReader.Core.Hosting;
using MdReader.Shell.Services;
using MdReader.Ui.WebView;

namespace MdReader.Ui.Spike;

internal static class SpikeExitCodes
{
    public const int Success = 0;
    public const int CommandLineError = 2;
    public const int WebViewUnavailable = 3;
    public const int CaptureFailed = 4;
}

/// <summary>
/// Everything the window needs, resolved before Avalonia starts (the spike has no DI container). It is also this shell's
/// <see cref="IAppHost"/>, which is what the shared layer uses to stop the app and to report a failure.
/// </summary>
internal sealed class SpikeContext(
    CommandLineOptions options,
    AppPaths paths,
    IAppLog log,
    string documentPath,
    WebViewEnvironmentFactory environment) : IAppHost
{
    internal static readonly TimeSpan CaptureTimeout = TimeSpan.FromSeconds(30);

    public CommandLineOptions Options { get; } = options;

    public AppPaths Paths { get; } = paths;

    public IAppLog Log { get; } = log;

    public string DocumentPath { get; } = documentPath;

    public WebViewEnvironmentFactory Environment { get; } = environment;

    public bool IsCapture => Options.CapturePath is not null;

    public IClassicDesktopStyleApplicationLifetime? Lifetime { get; set; }

    /// <summary>
    /// Always deferred: the WebView2 failure path can fire while Avalonia is still attaching the window to the visual
    /// tree, and shutting the lifetime down from inside that attach tears the tree apart underneath Avalonia.
    /// </summary>
    public void Shutdown(int exitCode) => Dispatcher.UIThread.Post(
        () => Lifetime?.Shutdown(exitCode), DispatcherPriority.Background);

    /// The spike has no window chrome for errors yet (phase 2 gets a real dialog); stderr is what a --capture run reads.
    public void ShowError(string title, string message) => Console.Error.WriteLine($"{title}: {message}");

    /// No modal prompts in a spike: the caller falls back to its non-interactive path.
    public bool ConfirmError(string title, string message)
    {
        ShowError(title, message);
        return false;
    }

    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
