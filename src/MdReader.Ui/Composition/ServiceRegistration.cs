using MdReader.Core.Cli;
using MdReader.Core.Diagnostics;
using MdReader.Core.Hosting;
using MdReader.Core.SingleInstance;
using MdReader.Edge;
using MdReader.Shell.Composition;
using MdReader.Shell.Services;
using MdReader.Shell.Threading;
using MdReader.Ui.Commands;
using MdReader.Ui.Services;
using MdReader.Ui.Threading;
using MdReader.Ui.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MdReader.Ui.Composition;

/// The Avalonia overlay on <see cref="ShellServices.AddShellCore"/> (§4.11): the UI thread, the application host, the
/// window, dialogs, clipboard, placement, theming glue and the WebView2 backend. Everything else is registered by the
/// shared shell, so this file is the whole difference between the two shells' composition roots.
public static class ServiceRegistration
{
    public static IServiceProvider Build(CommandLineOptions options, AppPaths paths, ISingleInstanceChannel? singleInstanceChannel)
        => Build(options, paths, singleInstanceChannel, log: null);

    /// Program passes the log it already created (the single-instance channel needs one before DI exists), so the process
    /// has exactly one FileAppLog writing the day's file.
    internal static ServiceProvider Build(CommandLineOptions options, AppPaths paths, ISingleInstanceChannel? singleInstanceChannel,
                                          IAppLog? log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);

        var services = new ServiceCollection();
        services.AddShellCore(options, paths, singleInstanceChannel, log);

        // The Avalonia shell itself. Built on the UI thread, so the dispatcher is this thread's.
        services.AddSingleton<IUiDispatcher>(new AvaloniaUiDispatcher());
        services.AddSingleton<IAppHost>(new AvaloniaAppHost(options));
        services.AddSingleton<ISystemThemeProbe, AvaloniaThemeProbe>();
        services.AddSingleton<AvaloniaThemeWindows>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<WindowActivator>();
        services.AddSingleton<WindowPlacementService>();
        services.AddSingleton<SessionEndWatcher>();
        services.AddSingleton<AvaloniaShortcutRouter>();
        services.AddSingleton<MainWindow>();

        // WebView2 backend (shared with the WPF shell through MdReader.Edge).
        services.AddSingleton<WebView2EnvironmentProvider>();
        services.AddSingleton<IWebView2EnvironmentProvider>(sp => sp.GetRequiredService<WebView2EnvironmentProvider>());
        services.AddSingleton<IWebViewEnvironmentProvider>(sp => sp.GetRequiredService<WebView2EnvironmentProvider>());

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    internal static FileAppLog CreateLog(AppPaths paths) => ShellServices.CreateLog(paths);
}
