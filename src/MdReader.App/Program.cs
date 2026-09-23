using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using MdReader.App.Composition;
using MdReader.App.Interop;
using MdReader.App.Services;
using MdReader.App.ViewModels;
using MdReader.App.Views;
using MdReader.Core.Cli;
using MdReader.Core.Diagnostics;
using MdReader.Core.Hosting;
using MdReader.Core.Settings;
using MdReader.Core.SingleInstance;
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
    private static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(3);

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
    private static ISingleInstanceChannel? AcquireSingleInstance(CommandLineOptions options, IAppLog log, out bool forwarded)
    {
        forwarded = false;
        var identity = new SingleInstanceIdentity(GetUserSid(), GetSessionId(), options.InstanceId);
        var channel = new SingleInstanceChannel(identity, log);
        if (channel.TryBecomePrimary())
        {
            return channel;
        }

        ForwardResult result;
        try
        {
            // Safe to block: no SynchronizationContext exists before the App is created (§5).
            result = channel.ForwardAsync(new OpenFilesRequest(options.Files, Environment.CurrentDirectory),
                                          pid => NativeMethods.AllowSetForegroundWindow(pid), ForwardTimeout)
                            .GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException
                                       or OperationCanceledException)
        {
            log.Write(AppLogLevel.Warning, Category, "Forwarding to the running instance failed", ex);
            result = ForwardResult.NoServer;
        }

        channel.Dispose();
        if (result == ForwardResult.Forwarded)
        {
            log.Write(AppLogLevel.Info, Category, $"Forwarded {options.Files.Count} file(s) to the running instance");
            forwarded = true;
            return null;
        }

        log.Write(AppLogLevel.Warning, Category, $"Couldn't forward to the running instance ({result}); running standalone");
        return null;
    }

    private static int RunApplication(CommandLineOptions options, AppPaths paths, ISingleInstanceChannel? channel, IAppLog log)
    {
        var app = new App();
        app.InitializeComponent();
        var crashHandler = new CrashHandler(options, paths, log);
        crashHandler.Install(app);

        ServiceProvider? services = null;
        var exitCode = ExitCodeCrash;
        try
        {
            services = ServiceRegistration.Build(options, paths, channel, log);
            var perf = services.GetRequiredService<PerfRecorder>();
            _ = services.GetRequiredService<SettingsCoordinator>();                  // synchronous settings load

            services.GetRequiredService<IWebViewEnvironmentProvider>().Start();      // as early as possible (§6)

            var theme = services.GetRequiredService<ThemeService>();                 // applies the palette
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
                capture.Start(app.Dispatcher);                                          // opens the first file only
            }
            else if (session is not null)
            {
                session.OpenStartupFiles(options.Files);                                 // saved tabs, then the CLI files
            }
            else
            {
                OpenFiles(services.GetRequiredService<IDocumentOpener>(), options.Files);
            }

            // One orderly teardown for every way out (window closed, Application.Shutdown, logoff/shutdown, Restart Manager):
            // it runs while the window and the browser processes still exist (§4.11).
            var shutdown = new OrderlyShutdown(window, services.GetRequiredService<WindowPlacementService>(),
                                               services.GetRequiredService<SettingsCoordinator>(),
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
            app.SessionEnding += (_, e) => shutdown.Run($"the Windows session is ending ({e.ReasonSessionEnding})");

            window.Show();
            perf.Mark(PerfMarks.WindowShown);

            if (channel is not null)
            {
                shutdown.StopForwarding = ServeForwardedFiles(app, window, channel, services.GetRequiredService<IDocumentOpener>(),
                                                              services.GetRequiredService<WindowActivator>(), options, log);
            }

            if (perf.IsEnabled)
            {
                services.GetRequiredService<UiStallMonitor>().Start(app.Dispatcher);
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

    /// Primary instance: accept files forwarded by later launches (§4.11) until the returned stop action runs (the first step
    /// of the orderly shutdown). No forwarded file is ever lost:
    /// - after the stop, the server is gone (late secondaries get NoServer) and a request racing with it is rejected before
    ///   its ACK (the secondary gets Rejected); either way the secondary opens its files itself;
    /// - a request already acknowledged but not yet opened (its dispatcher callback hadn't run, or never will because the
    ///   dispatcher is shutting down) is taken over by the stop action, which relaunches MdReader with those files once the
    ///   pipe and mutex are released, so the new process becomes the primary instance.
    private static Action ServeForwardedFiles(App app, MainWindow window, ISingleInstanceChannel channel, IDocumentOpener opener,
                                              WindowActivator activator, CommandLineOptions options, IAppLog log)
    {
        var gate = new Lock();
        var closing = false;                              // guarded by gate
        var pending = new List<OpenFilesRequest>();       // acknowledged, not yet opened; guarded by gate

        channel.FilesReceived += (_, request) =>
        {
            // Pipe server thread, before the ACK: throwing makes the channel reject the request (the sender opens standalone).
            lock (gate)
            {
                if (closing)
                {
                    log.Write(AppLogLevel.Info, Category, $"Refusing {request.Files.Count} forwarded file(s): MdReader is closing");
                    throw new InvalidOperationException("MdReader is closing; the request is refused so the sender opens the files itself.");
                }

                pending.Add(request);
            }

            app.Dispatcher.InvokeAsync(() =>
            {
                bool relaunch;
                lock (gate)
                {
                    if (!pending.Remove(request))
                    {
                        return;   // already taken over by the stop action
                    }

                    relaunch = closing;
                }

                if (relaunch)
                {
                    RelaunchWithFiles(options, request.Files, log);
                    return;
                }

                log.Write(AppLogLevel.Info, Category, $"Received {request.Files.Count} file(s) from another instance");
                OpenFiles(opener, request.Files);
                activator.Activate(window);
            });
        };

        channel.StartServer();
        return () =>
        {
            OpenFilesRequest[] orphaned;
            lock (gate)
            {
                closing = true;
                orphaned = [.. pending];
                pending.Clear();
            }

            channel.Dispose();   // stops the accept loop and releases the pipe name and mutex; Program disposes again (no-op)
            foreach (var request in orphaned)
            {
                RelaunchWithFiles(options, request.Files, log);
            }
        };
    }

    /// Opens forwarded files that arrived while this instance was shutting down in a new MdReader process. It keeps the
    /// isolation options (--profile-dir, --instance-id, --theme; never --capture/--perf-log) so tests stay isolated; it finds
    /// no running server and becomes the primary instance (or runs standalone).
    private static void RelaunchWithFiles(CommandLineOptions options, IReadOnlyList<string> files, IAppLog log)
    {
        if (files.Count == 0)
        {
            return;   // an "activate only" request: nothing to hand over
        }

        try
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The MdReader executable path is unknown.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
            if (options.ProfileDirectory is { } profile)
            {
                start.ArgumentList.Add("--profile-dir");
                start.ArgumentList.Add(profile);
            }

            if (!string.Equals(options.InstanceId, CommandLineOptions.DefaultInstanceId, StringComparison.Ordinal))
            {
                start.ArgumentList.Add("--instance-id");
                start.ArgumentList.Add(options.InstanceId);
            }

            if (options.ThemeOverride is { } theme)
            {
                start.ArgumentList.Add("--theme");
                start.ArgumentList.Add(theme.ToString().ToLowerInvariant());
            }

            start.ArgumentList.Add("--");
            foreach (var file in files)
            {
                start.ArgumentList.Add(file);
            }

            using var process = Process.Start(start);
            log.Write(AppLogLevel.Info, Category,
                $"MdReader is closing: relaunched {files.Count} forwarded file(s) in a new process (pid {process?.Id})");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            log.Write(AppLogLevel.Error, Category, $"Couldn't relaunch MdReader for {files.Count} forwarded file(s)", ex);
        }
    }

    /// Shutdown sequence (§4.11), run once, on the UI thread, before WPF destroys the main window: stop serving forwarded
    /// files → record the window placement → freeze the tab session (so closing the tabs below doesn't empty it) → save the
    /// settings → close every tab (find → session → WebView). Tearing the
    /// WebViews down here matters on logoff/shutdown and Restart Manager requests: once the session is ending the WebView2
    /// controllers become unusable, and WPF's own window teardown would otherwise still call into them
    /// (CoreWebView2Controller.set_IsVisible → access violation, settings never saved).
    private sealed class OrderlyShutdown(Window window, WindowPlacementService placement, SettingsCoordinator settings,
                                         MainViewModel tabs, SessionService? session, IAppLog log)
    {
        private bool _done;

        public Action? StopForwarding { get; set; }

        public void Run(string reason)
        {
            if (_done)
            {
                return;
            }

            _done = true;
            log.Write(AppLogLevel.Info, Category, $"Shutting down: {reason}");
            Step("stop the pipe server", () => StopForwarding?.Invoke());
            Step("record the window placement", () => placement.Save(window));
            Step("record the open tabs", () => session?.Freeze());
            Step("save the settings", settings.Flush);
            Step("close the tabs", tabs.CloseAllTabs);
        }

        private void Step(string what, Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                log.Write(AppLogLevel.Error, Category, $"Couldn't {what} during shutdown", ex);
            }
        }
    }

    /// CLI and pipe: open every file, activate the first (§4.11).
    private static void OpenFiles(IDocumentOpener opener, IReadOnlyList<string> files)
    {
        for (var i = 0; i < files.Count; i++)
        {
            opener.Open(files[i], fragment: null, activate: i == 0);
        }
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
