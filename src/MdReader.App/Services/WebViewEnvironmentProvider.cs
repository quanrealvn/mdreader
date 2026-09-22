using System.Windows;
using System.Windows.Threading;
using MdReader.Core.Cli;
using MdReader.Core.Diagnostics;
using MdReader.Core.Hosting;
using Microsoft.Web.WebView2.Core;

namespace MdReader.App.Services;

/// Creates the single CoreWebView2Environment shared by every tab (§6). Started as early as possible on the UI thread;
/// the options MUST be identical in every MdReader process because processes share the user data folder.
/// If the environment can't be created (runtime missing or any other failure) the app shows a dialog (not in test mode)
/// and exits with code 3.
public sealed class WebViewEnvironmentProvider : IWebViewEnvironmentProvider
{
    internal const int ExitCodeWebViewUnavailable = 3;

    /// Disables ambient (integrated) authentication for every host: the allowlist "_" matches no real server, so a remote
    /// image such as https://fileserver/x.png (single-label host → Local Intranet zone) can't make Chromium answer a
    /// Negotiate/NTLM challenge with the user's credentials; such challenges surface as BasicAuthenticationRequested
    /// instead, which the WebView hardening cancels. Verified on runtime 153.0.4234.48: without the switch an ambient
    /// "Authorization: Negotiate" header is sent to http://localhost; with it none is. The pre-2020 name
    /// "--auth-server-whitelist" is ignored by that runtime. Constant: every process sharing the user data folder must
    /// create the environment with identical options.
    internal const string AdditionalBrowserArguments = "--auth-server-allowlist=_";

    private const string Category = "WebView2";
    private const string RuntimeDownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    private readonly AppPaths _paths;
    private readonly CommandLineOptions _options;
    private readonly IAppLog _log;
    private readonly IPerfRecorder _perf;
    private readonly IExternalLauncher _launcher;
    private Task<CoreWebView2Environment>? _environment;
    private Dispatcher? _dispatcher;

    public WebViewEnvironmentProvider(AppPaths paths, CommandLineOptions options, IAppLog log, IPerfRecorder perf,
                                      IExternalLauncher launcher)
    {
        _paths = paths;
        _options = options;
        _log = log;
        _perf = perf;
        _launcher = launcher;
    }

    public void Start()
    {
        if (_environment is not null)
        {
            return;
        }

        _dispatcher = Dispatcher.CurrentDispatcher;
        _environment = CreateEnvironment();
        _environment.ContinueWith(OnCreated, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public Task<CoreWebView2Environment> GetAsync()
    {
        if (_environment is null)
        {
            Start();
        }

        return _environment!;
    }

    private Task<CoreWebView2Environment> CreateEnvironment()
    {
        try
        {
            var options = new CoreWebView2EnvironmentOptions
            {
                ScrollBarStyle = CoreWebView2ScrollbarStyle.FluentOverlay,
                AreBrowserExtensionsEnabled = false,
                AdditionalBrowserArguments = AdditionalBrowserArguments,
            };
            _log.Write(AppLogLevel.Debug, Category, $"Creating environment; user data folder {_paths.WebView2UserDataFolder}");
            return CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: _paths.WebView2UserDataFolder,
                                                       options: options);
        }
        catch (Exception ex)
        {
            return Task.FromException<CoreWebView2Environment>(ex);
        }
    }

    private void OnCreated(Task<CoreWebView2Environment> task)
    {
        if (task.IsCompletedSuccessfully)
        {
            _perf.Mark(PerfMarks.WebViewEnvironmentReady);
            _log.Write(AppLogLevel.Info, Category, "Environment ready");
            return;
        }

        var exception = task.Exception?.GetBaseException() ?? new OperationCanceledException("Environment creation was canceled.");
        _log.Write(AppLogLevel.Error, Category, "Couldn't create the WebView2 environment", exception);
        _dispatcher?.BeginInvoke(() => ReportFailureAndExit(exception));
    }

    private void ReportFailureAndExit(Exception exception)
    {
        if (!_options.IsTestMode)
        {
            var owner = DialogService.GetOwner();
            if (exception is WebView2RuntimeNotFoundException)
            {
                const string message = "MdReader needs the Microsoft Edge WebView2 Runtime to display documents, and it isn't installed.\n\n"
                                       + "Open the download page now?";
                var answer = owner is not null
                    ? MessageBox.Show(owner, message, "MdReader needs the WebView2 Runtime", MessageBoxButton.YesNo, MessageBoxImage.Error)
                    : MessageBox.Show(message, "MdReader needs the WebView2 Runtime", MessageBoxButton.YesNo, MessageBoxImage.Error);
                if (answer == MessageBoxResult.Yes)
                {
                    _launcher.TryOpen(new Uri(RuntimeDownloadUrl));
                }
            }
            else
            {
                var message = "MdReader couldn't start the Microsoft Edge WebView2 Runtime that it uses to display documents.\n\n"
                              + $"{exception.Message}\n\nReinstalling or repairing the WebView2 Runtime usually fixes this: {RuntimeDownloadUrl}";
                if (owner is not null)
                {
                    MessageBox.Show(owner, message, "MdReader", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    MessageBox.Show(message, "MdReader", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        Application.Current?.Shutdown(ExitCodeWebViewUnavailable);
    }
}
