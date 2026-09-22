using System.Globalization;
using MdReader.App.Commands;
using MdReader.App.Services;
using MdReader.App.ViewModels;
using MdReader.App.Views;
using MdReader.Core.Cli;
using MdReader.Core.Diagnostics;
using MdReader.Core.Documents;
using MdReader.Core.Hosting;
using MdReader.Core.Paths;
using MdReader.Core.Rendering;
using MdReader.Core.Settings;
using MdReader.Core.SingleInstance;
using Microsoft.Extensions.DependencyInjection;

namespace MdReader.App.Composition;

/// Composition root (§4.11). Everything is a singleton; DocumentSession/DocumentTabViewModel are created per tab by
/// DocumentOpener via ActivatorUtilities, so every service they need must be registered here.
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

        // Host basics
        services.AddSingleton(options);
        services.AddSingleton(paths);
        if (log is null)
        {
            services.AddSingleton<IAppLog>(_ => CreateLog(paths));   // factory: disposed with the container
        }
        else
        {
            services.AddSingleton(log);
        }

        if (singleInstanceChannel is not null)
        {
            services.AddSingleton(singleInstanceChannel);
        }

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISettingsStore>(sp => new JsonSettingsStore(paths.SettingsFile, sp.GetRequiredService<IAppLog>()));
        services.AddSingleton(sp => new SettingsCoordinator(sp.GetRequiredService<ISettingsStore>(), sp.GetRequiredService<IAppLog>(),
                                                            sp.GetRequiredService<TimeProvider>()));

        // Core pipeline
        services.AddSingleton<IFileSystemProbe>(PhysicalFileSystemProbe.Instance);
        services.AddSingleton<IMarkdownRenderer>(sp => new MarkdownRenderer(sp.GetRequiredService<IFileSystemProbe>()));
        services.AddSingleton(sp => new LinkClassifier(sp.GetRequiredService<IFileSystemProbe>()));
        services.AddSingleton(sp => new ResourceRootResolver(sp.GetRequiredService<IFileSystemProbe>()));
        services.AddSingleton<IDocumentLoader>(sp => new DocumentLoader(
            new DocumentLoaderOptions { FallbackCodePage = CultureInfo.CurrentCulture.TextInfo.ANSICodePage },
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IDocumentWatcherFactory>(sp => new DocumentWatcherFactory(sp.GetRequiredService<IAppLog>(),
                                                                                         sp.GetRequiredService<TimeProvider>()));

        // App services
        services.AddSingleton<ThemeService>();
        services.AddSingleton<IThemeService>(sp => sp.GetRequiredService<ThemeService>());
        services.AddSingleton<IWebViewEnvironmentProvider, WebViewEnvironmentProvider>();
        services.AddSingleton<IDocumentOpener, DocumentOpener>();
        services.AddSingleton(sp => new Lazy<IDocumentOpener>(sp.GetRequiredService<IDocumentOpener>));
        services.AddSingleton<IExternalLauncher, ExternalLauncher>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IClipboardService, ClipboardService>();
        services.AddSingleton<UiStallMonitor>();
        services.AddSingleton<PerfRecorder>();
        services.AddSingleton<IPerfRecorder>(sp => sp.GetRequiredService<PerfRecorder>());
        services.AddSingleton<WindowActivator>();
        services.AddSingleton<WindowPlacementService>();
        services.AddSingleton<CaptureRunner>();
        services.AddSingleton<SessionService>();

        // Shell
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<ITabHost>(sp => sp.GetRequiredService<MainViewModel>());
        services.AddSingleton<IStatusNotifier>(sp => sp.GetRequiredService<MainViewModel>());
        services.AddSingleton<KeyboardShortcuts>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    internal static FileAppLog CreateLog(AppPaths paths) =>
#if DEBUG
        new(paths.LogsFolder, AppLogLevel.Debug);
#else
        new(paths.LogsFolder, AppLogLevel.Info);
#endif
}
