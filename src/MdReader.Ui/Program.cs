using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using MdReader.Core.Cli;
using MdReader.Core.Diagnostics;
using MdReader.Core.Hosting;
using MdReader.Core.Settings;
using MdReader.Core.SingleInstance;
using MdReader.Shell.Hosting;
using MdReader.Shell.Services;
using MdReader.Shell.Threading;
using MdReader.Shell.ViewModels;
using MdReader.Ui.Composition;
using MdReader.Ui.Services;
using MdReader.Ui.Views;
using Microsoft.Extensions.DependencyInjection;

namespace MdReader.Ui;

/// Entry point implementing the startup sequence of ARCHITECTURE §6, the same one the WPF shell runs:
/// mainEntered → CLI (exit 2) → AppPaths → single instance (forward → exit 0) → Avalonia + DI + settings → WebView2
/// environment → theme + window + placement → reopen the last session + open CLI files → Show → pipe server → run loop.
/// Exit codes (§4.7): 0 ok/forwarded/help, 1 crash, 2 command line, 3 WebView2 unavailable, 4 capture failed.
public static partial class Program
{
    internal const int ExitCodeSuccess = 0;
    internal const int ExitCodeCrash = 1;
    internal const int ExitCodeCommandLine = 2;

    private const string Category = "Startup";
    private const string TestModeEnvironmentVariable = "MDREADER_TEST_MODE";

    /// <summary>
    /// STA is not optional: WebView2 is COM, and <c>CreateCoreWebView2ControllerAsync</c> completes through the same
    /// Win32 message loop Avalonia pumps.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        StartupClock.MarkMainEntered();
        ConfigurePlatform();

        var parsed = CommandLineParser.Parse(args, Environment.CurrentDirectory, Environment.GetEnvironmentVariable);
        if (!parsed.IsSuccess)
        {
            return ReportCommandLineError(args, parsed.Error ?? "The command line isn't valid.");
        }

        CommandLineOptions options = parsed.Options!;
        if (options.ShowHelp)
        {
            ShowHelp(options);
            return ExitCodeSuccess;
        }

        AppPaths paths = AppPaths.Create(options.ProfileDirectory, AppContext.BaseDirectory);
        EnsureProfileDirectory(options);
        FileAppLog log = ServiceRegistration.CreateLog(paths);
        try
        {
            log.Write(AppLogLevel.Info, Category,
                $"MdReader {typeof(Program).Assembly.GetName().Version} (Avalonia shell) starting: pid {Environment.ProcessId}, "
                + $"instance '{options.InstanceId}', test mode {options.IsTestMode}, {options.Files.Count} file(s)"
                + (options.CapturePath is null ? "" : ", capture")
                + (options.PerfLogPath is null ? "" : ", perf log"));

            ISingleInstanceChannel? channel = null;
            if (options.CapturePath is null)   // --capture implies standalone: never forward, never serve
            {
                channel = AcquireSingleInstance(options, log, out bool forwarded);
                if (forwarded)
                {
                    return ExitCodeSuccess;
                }
            }

            try
            {
                return RunApplication(args, options, paths, channel, log);
            }
            finally
            {
                channel?.Dispose();
            }
        }
        finally
        {
            log.Write(AppLogLevel.Info, Category, "Exiting");
            log.Dispose();
        }
    }

    private static ISingleInstanceChannel? AcquireSingleInstance(CommandLineOptions options, IAppLog log, out bool forwarded) =>
        SingleInstanceGate.Acquire(options, new SingleInstanceIdentity(CurrentUserId(), CurrentSessionId(), options.InstanceId),
                                   AllowSetForegroundWindow, log, out forwarded);

    private static int RunApplication(string[] args, CommandLineOptions options, AppPaths paths, ISingleInstanceChannel? channel,
                                      IAppLog log)
    {
        // SetupWithLifetime initializes Avalonia on this thread without running the loop, so everything below happens
        // in the same order as in the WPF shell and the window is built before anything is shown.
        var lifetime = new ClassicDesktopStyleApplicationLifetime { ShutdownMode = ShutdownMode.OnMainWindowClose, Args = args };
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().SetupWithLifetime(lifetime);

        var crashHandler = new CrashHandler(new CrashReporter(options, paths, new AvaloniaAppHost(options), log));
        crashHandler.Install();

        ServiceProvider? services = null;
        int exitCode = ExitCodeCrash;
        try
        {
            services = ServiceRegistration.Build(options, paths, channel, log);
            var perf = services.GetRequiredService<PerfRecorder>();
            _ = services.GetRequiredService<SettingsCoordinator>();                  // synchronous settings load

            services.GetRequiredService<IWebViewEnvironmentProvider>().Start();      // as early as possible (§6)

            var theme = services.GetRequiredService<ThemeService>();                 // resolves the effective theme
            _ = services.GetRequiredService<AvaloniaThemeWindows>();                 // applies the variant, follows changes
            if (options.ThemeOverride is { } themeOverride)
            {
                theme.ApplySessionOverride(themeOverride);
            }

            // The last session is restored and recorded by the primary instance only (never with --capture, which has no
            // channel); its files' existence checks run in the background while the window is built.
            var session = channel is not null ? services.GetRequiredService<SessionService>() : null;
            session?.BeginRestore();

            var window = services.GetRequiredService<MainWindow>();                  // placement restored, DWM attached
            lifetime.MainWindow = window;

            var capture = services.GetRequiredService<CaptureRunner>();
            if (capture.IsEnabled)
            {
                capture.Start();                                                        // opens the first file only
            }
            else if (session is not null)
            {
                session.OpenStartupFiles(options.Files);                                 // saved tabs, then the CLI files
            }
            else
            {
                SingleInstanceGate.OpenFiles(services.GetRequiredService<IDocumentOpener>(), options.Files);
            }

            // One orderly teardown for every way out (window closed, Shutdown, logoff/shutdown, Restart Manager): it runs
            // while the window and the browser processes still exist (§4.11).
            var placement = services.GetRequiredService<WindowPlacementService>();
            var mainViewModel = services.GetRequiredService<MainViewModel>();
            var shutdown = new OrderlyShutdown(() => placement.Save(window), services.GetRequiredService<SettingsCoordinator>(),
                                               mainViewModel, session, log);
            window.Closing += (_, e) =>
            {
                if (e.Cancel)
                {
                    return;
                }

                if (!mainViewModel.ConfirmCloseAllTabs())
                {
                    e.Cancel = true;   // unsaved edits and the user chose Cancel (§4.10)
                    return;
                }

                shutdown.Run("the main window is closing");
            };
            services.GetRequiredService<SessionEndWatcher>().Attach(window, reason => shutdown.Run(reason, saveUnsavedTabs: true));

            // The platform's own shell furniture, once the view models and the window exist: on macOS the native menu
            // bar and the application-delegate messages that hand this instance files to open.
            AttachPlatformShell(window, mainViewModel, channel, log);

            window.Show();   // never activated with --capture or in test mode; the window decides that itself
            perf.Mark(PerfMarks.WindowShown);

            if (channel is not null)
            {
                var activator = services.GetRequiredService<WindowActivator>();
                shutdown.StopForwarding = SingleInstanceGate.Serve(channel, options, services.GetRequiredService<IUiDispatcher>(),
                                                                   services.GetRequiredService<IDocumentOpener>(),
                                                                   () => activator.Activate(window), log);
            }

            if (perf.IsEnabled)
            {
                services.GetRequiredService<UiStallMonitor>().Start();
            }

            exitCode = lifetime.Start(args);   // the window is already open; this runs the loop
        }
        catch (Exception ex)
        {
            crashHandler.ReportFatal(ex);
            exitCode = ExitCodeCrash;
        }
        finally
        {
            Shutdown(services, log);
        }

        log.Write(AppLogLevel.Info, Category, $"Exit code {exitCode}");
        return exitCode;
    }

    private static void Shutdown(ServiceProvider? services, IAppLog log)
    {
        if (services is null)
        {
            return;
        }

        try
        {
            services.GetService<UiStallMonitor>()?.Dispose();
            services.GetService<PerfRecorder>()?.WriteReportIfPending();
            services.GetService<SettingsCoordinator>()?.Flush();   // the only blocking save (§5)
        }
        catch (Exception ex)
        {
            log.Write(AppLogLevel.Error, Category, "Saving state at exit failed", ex);
        }

        try
        {
            services.Dispose();   // disposes MainViewModel (tabs/web views), ThemeService, SettingsCoordinator, …
        }
        catch (Exception ex)
        {
            log.Write(AppLogLevel.Error, Category, "Disposing services at exit failed", ex);
        }
    }

    // ----- Command line errors and help (before Avalonia exists) -----

    private static int ReportCommandLineError(string[] args, string error)
    {
        bool testMode = IsTestModeRequested(args);
        WriteConsole(Console.Error, $"MdReader: {error}");
        LogCommandLineError(args, error, testMode);
        if (!testMode)
        {
            ShowStartupMessage("MdReader", $"{error}\n\n{CommandLineParser.UsageText}", isError: true);
        }

        return ExitCodeCommandLine;
    }

    private static void ShowHelp(CommandLineOptions options)
    {
        WriteConsole(Console.Out, CommandLineParser.UsageText);
        if (!options.IsTestMode)
        {
            ShowStartupMessage("MdReader — command line", CommandLineParser.UsageText, isError: false);
        }
    }

    /// The parser failed, so the options are unknown: detect test mode and the profile directory from the raw arguments
    /// to decide whether a message box may be shown and where the log goes (never the real profile in test mode).
    private static bool IsTestModeRequested(IReadOnlyList<string> args) =>
        Environment.GetEnvironmentVariable(TestModeEnvironmentVariable) == "1" || FindRawOption(args, "instance-id", out _);

    private static void LogCommandLineError(IReadOnlyList<string> args, string error, bool testMode)
    {
        try
        {
            string? profile = null;
            if (FindRawOption(args, "profile-dir", out string? rawProfile) && !string.IsNullOrWhiteSpace(rawProfile))
            {
                profile = Path.GetFullPath(rawProfile, Environment.CurrentDirectory);
            }
            else if (testMode)
            {
                return;   // no profile given: don't write into the user's real log folder from a test
            }

            AppPaths paths = AppPaths.Create(profile, AppContext.BaseDirectory);
            using FileAppLog log = ServiceRegistration.CreateLog(paths);
            log.Write(AppLogLevel.Error, Category, $"Invalid command line: {error}");
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            // Logging is best effort here; the exit code and stderr already report the error.
        }
    }

    private static bool FindRawOption(IReadOnlyList<string> args, string name, out string? value)
    {
        value = null;
        string flag = "--" + name;
        for (var i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (arg == "--")
            {
                break;
            }

            if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase))
            {
                value = i + 1 < args.Count ? args[i + 1] : null;
                return true;
            }

            if (arg.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
            {
                value = arg[(flag.Length + 1)..];
                return true;
            }
        }

        return false;
    }

    private static void WriteConsole(TextWriter writer, string text)
    {
        try
        {
            writer.WriteLine(text);
            writer.Flush();
        }
        catch (IOException)
        {
            // No usable console (WinExe without redirected output).
        }
    }

    // ----- Environment -----

    private static void EnsureProfileDirectory(CommandLineOptions options)
    {
        if (options.ProfileDirectory is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(options.ProfileDirectory);   // "created if missing" (§4.7)
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Settings/log writers report their own failures; the app still starts.
        }
    }

    /// <summary>
    /// The first thing the process does, before the command line is even parsed: anything that has to be settled
    /// before a document can be rendered. On macOS that is the URL scheme the viewer page and the document's
    /// resources are served over, which every rendered image URL is built from.
    /// </summary>
    private static partial void ConfigurePlatform();

    /// The account this process runs as: the SID on Windows, the uid on POSIX. Keeps one user's instance out of
    /// another's; unused where the OS manages instances itself.
    private static partial string CurrentUserId();

    /// The login session; unused where the OS manages instances itself.
    private static partial int CurrentSessionId();

    /// Lets the instance that is about to be handed our files take the foreground. A no-op where that needs no
    /// permission.
    private static partial void AllowSetForegroundWindow(int processId);

    /// A message before the UI framework exists: the command line was wrong, or --help was asked for.
    private static partial void ShowStartupMessage(string title, string message, bool isError);

    /// <summary>
    /// The shell furniture only one platform has. On Windows there is none — the toolbar and the shortcut router are
    /// the whole UI. On macOS it is the native menu bar and the application-delegate messages that deliver files.
    /// </summary>
    private static partial void AttachPlatformShell(MainWindow window, MainViewModel viewModel,
                                                    ISingleInstanceChannel? channel, IAppLog log);
}
