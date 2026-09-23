using Avalonia.Controls.ApplicationLifetimes;
using MdReader.Avalonia.WebView;
using MdReader.Core.Cli;
using MdReader.Core.Diagnostics;
using MdReader.Core.Hosting;

namespace MdReader.Avalonia.Spike;

internal static class SpikeExitCodes
{
    public const int Success = 0;
    public const int CommandLineError = 2;
    public const int WebViewUnavailable = 3;
    public const int CaptureFailed = 4;
}

/// <summary>Everything the window needs, resolved before Avalonia starts (the spike has no DI container).</summary>
internal sealed class SpikeContext(
    CommandLineOptions options,
    AppPaths paths,
    IAppLog log,
    string documentPath,
    WebViewEnvironmentFactory environment)
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
    public void Exit(int exitCode) => global::Avalonia.Threading.Dispatcher.UIThread.Post(
        () => Lifetime?.Shutdown(exitCode), global::Avalonia.Threading.DispatcherPriority.Background);
}
