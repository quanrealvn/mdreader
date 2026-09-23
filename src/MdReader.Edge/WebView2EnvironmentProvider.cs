using MdReader.Core.Diagnostics;
using MdReader.Core.Hosting;
using MdReader.Shell.Services;
using MdReader.Shell.Threading;
using Microsoft.Web.WebView2.Core;

namespace MdReader.Edge;

/// <summary>The WebView2 environment, for the shell code that actually creates web views.</summary>
public interface IWebView2EnvironmentProvider : IWebViewEnvironmentProvider
{
    /// Same task every time; faults with WebView2RuntimeNotFoundException etc.
    Task<CoreWebView2Environment> GetAsync();
}

/// Creates the single CoreWebView2Environment shared by every tab (§6). Started as early as possible on the UI thread;
/// the options MUST be identical in every MdReader process because processes share the user data folder — which is why
/// this lives in the shared WebView2 backend rather than once per shell.
/// If the environment can't be created (runtime missing or any other failure) the app shows a dialog (not in test mode)
/// and exits with code 3.
public sealed class WebView2EnvironmentProvider : IWebView2EnvironmentProvider
{
    internal const int ExitCodeWebViewUnavailable = 3;

    /// Disables ambient (integrated) authentication for every host: the allowlist "_" matches no real server, so a remote
    /// image such as https://fileserver/x.png (single-label host → Local Intranet zone) can't make Chromium answer a
    /// Negotiate/NTLM challenge with the user's credentials; such challenges surface as BasicAuthenticationRequested
    /// instead, which the WebView hardening cancels. Verified on runtime 153.0.4234.48: without the switch an ambient
    /// "Authorization: Negotiate" header is sent to http://localhost; with it none is. The pre-2020 name
    /// "--auth-server-whitelist" is ignored by that runtime. Constant: every process sharing the user data folder must
    /// create the environment with identical options.
    public const string AdditionalBrowserArguments = "--auth-server-allowlist=_";

    private const string Category = "WebView2";
    private const string RuntimeDownloadUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";

    private readonly AppPaths _paths;
    private readonly IAppHost _host;
    private readonly IUiDispatcher _dispatcher;
    private readonly IAppLog _log;
    private readonly IPerfRecorder _perf;
    private readonly IExternalLauncher _launcher;
    private Task<CoreWebView2Environment>? _environment;

    public WebView2EnvironmentProvider(AppPaths paths, IAppHost host, IUiDispatcher dispatcher, IAppLog log, IPerfRecorder perf,
                                       IExternalLauncher launcher)
    {
        _paths = paths;
        _host = host;
        _dispatcher = dispatcher;
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

    public Task WhenReadyAsync() => GetAsync();

    /// <summary>
    /// The one place the environment options are written. Every MdReader process that shares the user data folder — both
    /// shells and any future tool — must create the environment with exactly these options, so nothing may fork them.
    /// </summary>
    public static Task<CoreWebView2Environment> CreateAsync(string userDataFolder)
    {
        var options = new CoreWebView2EnvironmentOptions
        {
            ScrollBarStyle = CoreWebView2ScrollbarStyle.FluentOverlay,
            AreBrowserExtensionsEnabled = false,
            AdditionalBrowserArguments = AdditionalBrowserArguments,
        };
        return CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, userDataFolder: userDataFolder, options: options);
    }

    private Task<CoreWebView2Environment> CreateEnvironment()
    {
        try
        {
            _log.Write(AppLogLevel.Debug, Category, $"Creating environment; user data folder {_paths.WebView2UserDataFolder}");
            return CreateAsync(_paths.WebView2UserDataFolder);
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
        _dispatcher.Post(() => ReportFailureAndExit(exception));
    }

    private void ReportFailureAndExit(Exception exception)
    {
        if (exception is WebView2RuntimeNotFoundException)
        {
            const string message = "MdReader needs the Microsoft Edge WebView2 Runtime to display documents, and it isn't installed.\n\n"
                                   + "Open the download page now?";
            if (_host.ConfirmError("MdReader needs the WebView2 Runtime", message))
            {
                _launcher.TryOpen(new Uri(RuntimeDownloadUrl));
            }
        }
        else
        {
            var message = "MdReader couldn't start the Microsoft Edge WebView2 Runtime that it uses to display documents.\n\n"
                          + $"{exception.Message}\n\nReinstalling or repairing the WebView2 Runtime usually fixes this: {RuntimeDownloadUrl}";
            _host.ShowError("MdReader", message);
        }

        _host.Shutdown(ExitCodeWebViewUnavailable);
    }
}
