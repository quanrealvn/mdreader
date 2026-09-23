using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using MdReader.App.Composition;
using MdReader.App.Interop;
using MdReader.App.Services;
using MdReader.Shell.Services;
using MdReader.Shell.ViewModels;
using MdReader.App.Views;
using MdReader.Core.Cli;
using MdReader.Core.Diagnostics;
using MdReader.Core.Hosting;
using MdReader.Core.Settings;
using MdReader.Core.SingleInstance;
using MdReader.Shell.Hosting;
using MdReader.Shell.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace MdReader.App;

/// Custom entry point implementing the startup sequence of ARCHITECTURE §6:
/// mainEntered → CLI (exit 2) → AppPaths → single instance (forward → exit 0) → App + DI + settings → WebView2 environment
/// → theme + window + placement → reopen the last session + open CLI files → Show → pipe server → Application.Run.
/// Exit codes (§4.7): 0 ok/forwarded/help, 1 crash, 2 command line, 3 WebView2 unavailable, 4 capture failed.
public static class Program
{
    internal const int ExitCodeSuccess = 0;
    internal const int ExitCodeCrash = 1;
    internal const int ExitCodeCommandLine = 2;

    private const string Category = "Startup";
    private const string TestModeEnvironmentVariable = "MDREADER_TEST_MODE";

    [STAThread]
    static int Main(string[] args)
    {
        StartupClock.MarkMainEntered();

        var parsed = CommandLineParser.Parse(args, Environment.CurrentDirectory, Environment.GetEnvironmentVariable);
        if (!parsed.IsSuccess)
        {
            return ReportCommandLineError(args, parsed.Error ?? "The command line isn't valid.");
        }

        var options = parsed.Options!;
        if (options.ShowHelp)
        {
            ShowHelp(options);
            return ExitCodeSuccess;
        }

        var paths = AppPaths.Create(options.ProfileDirectory, AppContext.BaseDirectory);
        EnsureProfileDirectory(options);
        var log = ServiceRegistration.CreateLog(paths);
        try
        {
            log.Write(AppLogLevel.Info, Category,
                $"MdReader {typeof(Program).Assembly.GetName().Version} starting: pid {Environment.ProcessId}, instance '{options.InstanceId}', "
                + $"test mode {options.IsTestMode}, {options.Files.Count} file(s)"
                + (options.CapturePath is null ? "" : ", capture")
                + (options.PerfLogPath is null ? "" : ", perf log"));

            ISingleInstanceChannel? channel = null;
            if (options.CapturePath is null)   // --capture implies standalone: never forward, never serve
            {
                channel = AcquireSingleInstance(options, log, out var forwarded);
                if (forwarded)
                {
                    return ExitCodeSuccess;
                }
            }

            try
            {
                return RunApplication(options, paths, channel, log);
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

    /// Returns the channel when this process is the primary instance; null when it forwarded (forwarded = true) or has to
    /// run standalone because forwarding failed.
    private static ISingleInstanceChannel? AcquireSingleInstance(CommandLineOptions options, IAppLog log, out bool forwarded) =>
        SingleInstanceGate.Acquire(options, new SingleInstanceIdentity(GetUserSid(), GetSessionId(), options.InstanceId),
                                   pid => NativeMethods.AllowSetForegroundWindow(pid), log, out forwarded);

    private static int RunApplication(CommandLineOptions options, AppPaths paths, ISingleInstanceChannel? channel, IAppLog log)
    {
        var app = new App();
        app.InitializeComponent();
        var crashHandler = new CrashHandler(new CrashReporter(options, paths, new WpfAppHost(options), log));
        crashHandler.Install(app);

        ServiceProvider? services = null;
        var exitCode = ExitCodeCrash;
        try
        {
            services = ServiceRegistration.Build(options, paths, channel, log);
            var perf = services.GetRequiredService<PerfRecorder>();
            _ = services.GetRequiredService<SettingsCoordinator>();                  // synchronous settings load

            services.GetRequiredService<IWebViewEnvironmentProvider>().Start();      // as early as possible (§6)

            var theme = services.GetRequiredService<ThemeService>();                 // resolves the effective theme
            _ = services.GetRequiredService<WpfThemeWindows>();                      // applies the palette, follows changes
            if (options.ThemeOverride is { } themeOverride)
            {
                theme.ApplySessionOverride(themeOverride);
            }

            // The last session is restored and recorded by the primary instance only (never with --capture, which has no
            // channel); its files' existence checks run in the background while the window is built.
            var session = channel is not null ? services.GetRequiredService<SessionService>() : null;
            session?.BeginRestore();

            var window = services.GetRequiredService<MainWindow>();                  // placement restored, DWM attached
            app.MainWindow = window;

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

            // One orderly teardown for every way out (window closed, Application.Shutdown, logoff/shutdown, Restart Manager):
            // it runs while the window and the browser processes still exist (§4.11).
            var placement = services.GetRequiredService<WindowPlacementService>();
            var shutdown = new OrderlyShutdown(() => placement.Save(window), services.GetRequiredService<SettingsCoordinator>(),
                                               services.GetRequiredService<MainViewModel>(), session, log);
            var mainViewModel = services.GetRequiredService<MainViewModel>();
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
            app.SessionEnding += (_, e) =>
                shutdown.Run($"the Windows session is ending ({e.ReasonSessionEnding})", saveUnsavedTabs: true);

            // Screenshots (--capture) and automated runs (--instance-id / MDREADER_TEST_MODE) must never steal focus from
            // whatever the user is doing. ShowActivated only affects this first Show: a second instance forwarding a file
            // still brings the window forward through WindowActivator.
            if (options.CapturePath is not null || options.IsTestMode)
            {
                window.ShowActivated = false;
            }

            window.Show();
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

            exitCode = app.Run();
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
            services.Dispose();   // disposes MainViewModel (tabs/WebViews), ThemeService, SettingsCoordinator, …
        }
        catch (Exception ex)
        {
            log.Write(AppLogLevel.Error, Category, "Disposing services at exit failed", ex);
        }
    }

    // ----- Command line errors and help (before the App exists) -----

    private static int ReportCommandLineError(string[] args, string error)
    {
        var testMode = IsTestModeRequested(args);
        WriteConsole(Console.Error, $"MdReader: {error}");
        LogCommandLineError(args, error, testMode);
        if (!testMode)
        {
            MessageBox.Show($"{error}\n\n{CommandLineParser.UsageText}", "MdReader", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        return ExitCodeCommandLine;
    }

    private static void ShowHelp(CommandLineOptions options)
    {
        WriteConsole(Console.Out, CommandLineParser.UsageText);
        if (!options.IsTestMode)
        {
            MessageBox.Show(CommandLineParser.UsageText, "MdReader — command line", MessageBoxButton.OK, MessageBoxImage.Information);
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
            if (FindRawOption(args, "profile-dir", out var rawProfile) && !string.IsNullOrWhiteSpace(rawProfile))
            {
                profile = Path.GetFullPath(rawProfile, Environment.CurrentDirectory);
            }
            else if (testMode)
            {
                return;   // no profile given: don't write into the user's real log folder from a test
            }

            var paths = AppPaths.Create(profile, AppContext.BaseDirectory);
            using var log = ServiceRegistration.CreateLog(paths);
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
        var flag = "--" + name;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
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

    private static string GetUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!.Value;
    }

    private static int GetSessionId()
    {
        using var process = Process.GetCurrentProcess();
        return process.SessionId;
    }
}
