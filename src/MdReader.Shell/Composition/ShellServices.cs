using System.Globalization;
using MdReader.Core.Cli;
using MdReader.Core.Diagnostics;
using MdReader.Core.Documents;
using MdReader.Core.Hosting;
using MdReader.Core.Paths;
using MdReader.Core.Rendering;
using MdReader.Core.Settings;
using MdReader.Core.SingleInstance;
using MdReader.Shell.Commands;
using MdReader.Shell.Services;
using MdReader.Shell.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace MdReader.Shell.Composition;

/// <summary>
/// The platform-neutral half of the composition root (§4.11): everything that doesn't need a window, a handle, a dialog
/// or a web-view backend. Each shell adds its own overlay (<c>MdReader.App.Composition.ServiceRegistration</c>) with
/// <see cref="Threading.IUiDispatcher"/>, <see cref="IAppHost"/>, the dialogs, the clipboard, the web-view environment
/// and its window.
/// </summary>
/// <remarks>
/// Everything is a singleton; DocumentSession/DocumentTabViewModel are created per tab by DocumentOpener via
/// ActivatorUtilities, so every service they need must be registered here or in the shell's overlay.
/// </remarks>
public static class ShellServices
{
    public static IServiceCollection AddShellCore(this IServiceCollection services, CommandLineOptions options, AppPaths paths,
                                                  ISingleInstanceChannel? singleInstanceChannel, IAppLog? log)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(paths);

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

        // Shared shell services
        services.AddSingleton<ThemeService>();
        services.AddSingleton<IThemeService>(sp => sp.GetRequiredService<ThemeService>());
        services.AddSingleton<IExternalLauncher, ExternalLauncher>();
        services.AddSingleton<IDocumentOpener, DocumentOpener>();
        services.AddSingleton(sp => new Lazy<IDocumentOpener>(sp.GetRequiredService<IDocumentOpener>));
        services.AddSingleton<UiStallMonitor>();
        services.AddSingleton<PerfRecorder>();
        services.AddSingleton<IPerfRecorder>(sp => sp.GetRequiredService<PerfRecorder>());
        services.AddSingleton<CaptureRunner>();
        services.AddSingleton<SessionService>();
        services.AddSingleton<CrashReporter>();

        // Shell view models
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<ITabHost>(sp => sp.GetRequiredService<MainViewModel>());
        services.AddSingleton<IStatusNotifier>(sp => sp.GetRequiredService<MainViewModel>());
        services.AddSingleton<KeyboardShortcutRouter>();

        return services;
    }

    public static FileAppLog CreateLog(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return
#if DEBUG
            new(paths.LogsFolder, AppLogLevel.Debug);
#else
            new(paths.LogsFolder, AppLogLevel.Info);
#endif
    }
}
