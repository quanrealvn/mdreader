using System.IO;
using MdReader.Core.Diagnostics;
using MdReader.Core.Documents;
using MdReader.Core.Hosting;
using MdReader.Core.Paths;
using MdReader.Core.Protocol;
using MdReader.Core.Theming;
using MdReader.Shell.Documents;
using MdReader.Shell.Services;
using Microsoft.Web.WebView2.Core;

namespace MdReader.Edge;

/// <summary>Why a tab can't show its page at all (the shell shows its own error in place of the web view).</summary>
public sealed class WebViewHostFailedEventArgs(DocumentErrorKind kind, string detail, bool requiresNewWebView) : EventArgs
{
    public DocumentErrorKind Kind { get; } = kind;

    public string Detail { get; } = detail;

    /// The CoreWebView2 is closed (browser process gone): re-navigating can't help, only a new web view.
    public bool RequiresNewWebView { get; } = requiresNewWebView;
}

/// <summary>
/// <see cref="IWebViewChannel"/> for one WebView2 view: initialization, virtual host mappings, hardening,
/// origin-checked message intake, posting, and render-process crash recovery (ARCHITECTURE §4.10, §6, §7.1, §8.4).
/// All members are UI-thread only.
/// </summary>
/// <remarks>
/// The shell supplies the view itself through <see cref="IWebView2Surface"/> — a WPF <c>WebView2</c> control or a
/// <c>CoreWebView2Controller</c> in an Avalonia native host — and everything below this line is the same for both.
/// </remarks>
public sealed class WebView2Channel : IWebViewChannel, IDisposable
{
    private const string Category = "WebViewBridge";
    private const int MaxRenderProcessFailures = 3;
    private static readonly TimeSpan RenderProcessFailureWindow = TimeSpan.FromSeconds(60);

    private readonly IWebView2Surface _surface;
    private readonly IThemeService _theme;
    private readonly AppPaths _paths;
    private readonly IPerfRecorder _perf;
    private readonly IAppLog _log;
    private readonly TimeProvider _time;
    private readonly Queue<long> _renderProcessFailures = new();
    private readonly CoreWebView2Operations _operations;

    private CoreWebView2? _core;
    private string? _unmappedDocumentRoot;   // doc folder that didn't exist yet (DirectoryNotFoundException)
    private ulong? _pageNavigationId;
    private bool _failed;
    private bool _webViewDead;               // browser process exited: this CoreWebView2 is Closed for good
    private bool _disposed;

    public WebView2Channel(IWebView2Surface surface, IThemeService theme, AppPaths paths, IPerfRecorder perf, IAppLog log,
                           TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(surface);
        _surface = surface;
        _theme = theme;
        _paths = paths;
        _perf = perf;
        _log = log;
        _time = time;
        _operations = new CoreWebView2Operations(() => _disposed ? null : _core, log);
        _surface.ZoomFactorChanged += OnZoomFactorChanged;
    }

    public bool IsReady { get; private set; }

    public event EventHandler? Ready;

    public event EventHandler<WebMessage>? MessageReceived;

    public event EventHandler<IReadOnlyList<string>>? FilesDropped;

    /// Raised when the page can't be shown (protocol mismatch, viewer page failed to load, repeated renderer crashes,
    /// browser process gone). The view replaces the web view with its own error.
    public event EventHandler<WebViewHostFailedEventArgs>? HostFailed;

    /// True when local images can't be served because the resource root couldn't be mapped (path too long, etc.).
    public bool LocalResourcesUnavailable { get; private set; }

    public async Task InitializeAsync(CoreWebView2Environment environment, string resourceRoot)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_core is not null)
        {
            throw new InvalidOperationException("The WebView is already initialized.");
        }

        CoreWebView2 core = await _surface.EnsureCoreWebView2Async(environment);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _core = core;
        _perf.Mark(PerfMarks.WebViewReady);

        WebViewSecurity.Apply(core, _theme.EffectiveTheme, _log, OnLinkNavigation);

        // The app host must map, or nothing can be shown: let that exception reach the view.
        core.SetVirtualHostNameToFolderMapping(ProtocolConstants.AppHost, _paths.WebRoot, CoreWebView2HostResourceAccessKind.Deny);
        MapDocumentRoot(resourceRoot);

        // Handlers go in BEFORE the first navigation (§6). NavigationStarting here runs after the security filter.
        core.WebMessageReceived += OnWebMessageReceived;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.ProcessFailed += OnProcessFailed;

        NavigateToPage();
    }

    public void Post(string json)
    {
        if (_disposed)
        {
            return;
        }

        CoreWebView2? core = _core;
        if (!IsReady || core is null)
        {
            _log.Write(AppLogLevel.Debug, Category, $"Dropped a message because the page isn't ready: {WebViewSecurity.Describe(json)}.");
            return;
        }

        try
        {
            core.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Warning, Category, $"PostWebMessageAsJson failed: {WebViewSecurity.Describe(json)}.", ex);
        }
    }

    // ----- Capabilities (§4.10): find, print, capture, zoom and theme, so nothing outside needs the CoreWebView2 -----

    public Task<FindSession?> StartFindAsync(string term) => _operations.StartFindAsync(term);

    public void FindNext() => _operations.FindNext();

    public void FindPrevious() => _operations.FindPrevious();

    public void StopFind() => _operations.StopFind();

    public Task ShowPrintUiAsync() => _operations.ShowPrintUiAsync();

    public Task<bool> PrintToPdfAsync(string pdfPath) => _operations.PrintToPdfAsync(pdfPath);

    public Task CapturePreviewAsync(Stream pngDestination) => _operations.CapturePreviewAsync(pngDestination);

    public double Zoom => _surface.ZoomFactor;

    public void SetZoom(double zoomFactor) => _surface.ZoomFactor = zoomFactor;

    public event EventHandler? ZoomChanged;

    public void SetBackgroundColor(byte red, byte green, byte blue)
    {
        try
        {
            _surface.SetDefaultBackgroundColor(red, green, blue);
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Warning, Category, "Couldn't apply the theme to the WebView.", ex);
        }
    }

    public void SetPreferredColorScheme(AppTheme theme)
    {
        if (_core is { } core && !_disposed)
        {
            WebViewSecurity.ApplyPreferredColorScheme(core, theme, _log);
        }
    }

    /// Retries the doc-host mapping if the document folder didn't exist at initialization. Called before a render
    /// payload is posted, so a folder created later (file restored) still gets its images.
    public void EnsureResourceRootMapped()
    {
        if (_unmappedDocumentRoot is { } root && _core is not null && !_disposed)
        {
            MapDocumentRoot(root);
        }
    }

    /// True when <see cref="Restart"/> can recover (the CoreWebView2 exists and is alive). Otherwise the view has to
    /// replace the web view and this channel.
    public bool CanRestart => !_disposed && _core is not null && !_webViewDead;

    /// Clears the crash history and reloads the page (the shell error's "Try again").
    public void Restart()
    {
        if (!CanRestart)
        {
            return;
        }

        _failed = false;
        _renderProcessFailures.Clear();
        NavigateToPage();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsReady = false;
        _operations.Dispose();
        _surface.ZoomFactorChanged -= OnZoomFactorChanged;
        CoreWebView2? core = _core;
        _core = null;
        if (core is null)
        {
            return;
        }

        try
        {
            core.WebMessageReceived -= OnWebMessageReceived;
            core.NavigationStarting -= OnNavigationStarting;
            core.NavigationCompleted -= OnNavigationCompleted;
            core.ProcessFailed -= OnProcessFailed;
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Debug, Category, "Unsubscribing from CoreWebView2 events failed.", ex);
        }
    }

    private void OnZoomFactorChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            ZoomChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void MapDocumentRoot(string root)
    {
        CoreWebView2 core = _core!;
        _unmappedDocumentRoot = null;
        if (root.Length > ResourceRootResolver.MaxMappablePathLength)
        {
            LocalResourcesUnavailable = true;
            _log.Write(AppLogLevel.Warning, Category, $"Resource root is too long to map ({root.Length} chars); local images are blocked: {root}");
            return;
        }

        try
        {
            core.SetVirtualHostNameToFolderMapping(ProtocolConstants.DocHost, root, CoreWebView2HostResourceAccessKind.DenyCors);
            LocalResourcesUnavailable = false;
        }
        catch (DirectoryNotFoundException ex)
        {
            // The document's folder is missing: the load reports NotFound and the page shows that error.
            _unmappedDocumentRoot = root;
            _log.Write(AppLogLevel.Info, Category, $"Resource root doesn't exist (yet): {root}. {ex.Message}");
        }
        catch (Exception ex)
        {
            LocalResourcesUnavailable = true;
            _log.Write(AppLogLevel.Warning, Category, $"Couldn't map the resource root {root}; local images are blocked.", ex);
        }
    }

    private void NavigateToPage()
    {
        CoreWebView2? core = _core;
        if (core is null || _disposed)
        {
            return;
        }

        IsReady = false;
        string theme = _theme.EffectiveTheme == AppTheme.Dark ? "dark" : "light";
        try
        {
            core.Navigate(ProtocolConstants.PageUrl + "?theme=" + theme);
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, Category, "Navigating to the viewer page failed.", ex);
            Fail(DocumentErrorKind.RenderFailed, "The viewer page couldn't be opened. " + ex.Message);
        }
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        try
        {
            if (!e.Cancel && WebViewSecurity.IsPageUrl(e.Uri))
            {
                IsReady = false;   // a (re)load of our page: nothing may be posted until its 'ready'
                _pageNavigationId = e.NavigationId;
            }
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, Category, "NavigationStarting bookkeeping failed.", ex);
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        try
        {
            if (_pageNavigationId != e.NavigationId)
            {
                return;
            }

            int httpStatus = e.HttpStatusCode;
            if ((!e.IsSuccess && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled) || httpStatus >= 400)
            {
                _log.Write(AppLogLevel.Error, Category,
                    $"The viewer page failed to load (status {e.WebErrorStatus}, HTTP {httpStatus}). Web root: {_paths.WebRoot}");
                Fail(DocumentErrorKind.RenderFailed, $"The viewer page failed to load ({e.WebErrorStatus}, HTTP {httpStatus}).");
            }
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, Category, "NavigationCompleted handler failed.", ex);
        }
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string source = e.Source;
            if (!WebViewSecurity.IsAppOrigin(source))
            {
                _log.Write(AppLogLevel.Warning, Category, $"Ignored a web message from an unexpected origin: {WebViewSecurity.Shorten(source)}.");
                return;
            }

            string json = e.WebMessageAsJson;
            if (!ProtocolSerializer.TryDeserialize(json, out WebMessage? message, out string? error))
            {
                _log.Write(AppLogLevel.Warning, Category,
                    $"Dropped an invalid web message ({WebViewSecurity.ForLog(error, 300)}): {WebViewSecurity.Describe(json)}.");
                return;
            }

            switch (message)
            {
                case ReadyMessage ready:
                    OnReady(ready);
                    break;
                case DropMessage:
                    OnDrop(e);
                    break;
                default:
                    MessageReceived?.Invoke(this, message);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, Category, "Handling a web message failed.", ex);
        }
    }

    private void OnReady(ReadyMessage ready)
    {
        if (ready.Protocol != ProtocolConstants.Version)
        {
            IsReady = false;
            _log.Write(AppLogLevel.Error, Category, $"The page speaks protocol {ready.Protocol}; the host requires {ProtocolConstants.Version}.");
            Fail(DocumentErrorKind.RenderFailed, $"The viewer page is incompatible (protocol {ready.Protocol}, expected {ProtocolConstants.Version}).");
            return;
        }

        if (_failed)
        {
            return;
        }

        IsReady = true;
        _perf.Mark(PerfMarks.PageReady);
        Ready?.Invoke(this, EventArgs.Empty);
    }

    private void OnDrop(CoreWebView2WebMessageReceivedEventArgs e)
    {
        var paths = new List<string>();
        var objects = e.AdditionalObjects;
        if (objects is not null)
        {
            foreach (object item in objects)
            {
                if (item is CoreWebView2File file && !string.IsNullOrWhiteSpace(file.Path))
                {
                    paths.Add(file.Path);
                }
            }
        }

        _log.Write(AppLogLevel.Info, Category, $"Files dropped on the page: {paths.Count}.");
        if (paths.Count > 0)
        {
            FilesDropped?.Invoke(this, paths);
        }
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        try
        {
            CoreWebView2ProcessFailedKind kind = e.ProcessFailedKind;
            switch (kind)
            {
                case CoreWebView2ProcessFailedKind.RenderProcessExited:
                    IsReady = false;
                    _log.Write(AppLogLevel.Warning, Category, $"The page's render process exited ({e.Reason}, exit code {e.ExitCode}).");
                    long now = _time.GetTimestamp();
                    _renderProcessFailures.Enqueue(now);
                    while (_renderProcessFailures.Count > 0
                           && _time.GetElapsedTime(_renderProcessFailures.Peek(), now) > RenderProcessFailureWindow)
                    {
                        _renderProcessFailures.Dequeue();
                    }

                    if (_renderProcessFailures.Count >= MaxRenderProcessFailures)
                    {
                        Fail(DocumentErrorKind.RenderFailed, $"The page crashed {_renderProcessFailures.Count} times within a minute.");
                    }
                    else
                    {
                        NavigateToPage();   // re-posted on the next 'ready'
                    }

                    break;

                case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                    IsReady = false;
                    _log.Write(AppLogLevel.Error, Category, $"The WebView2 browser process exited ({e.Reason}, exit code {e.ExitCode}).");
                    // The WebView is now Closed for good; the view recreates it on "Try again".
                    Fail(DocumentErrorKind.RenderFailed, "The WebView2 browser process exited.", requiresNewWebView: true);
                    break;

                case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                    // Large documents can keep the renderer busy for a while; don't reload on this.
                    _log.Write(AppLogLevel.Warning, Category, "The page's render process is unresponsive.");
                    break;

                default:
                    _log.Write(AppLogLevel.Info, Category, $"A WebView2 helper process failed: {kind} ({e.Reason}).");
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, Category, "ProcessFailed handler failed.", ex);
        }
    }

    private void OnLinkNavigation(Uri uri, bool newTab)
    {
        if (_disposed)
        {
            return;
        }

        // Routed exactly like a clicked link, so the LinkClassifier rules (UNC, blocked hosts, …) apply.
        MessageReceived?.Invoke(this, new LinkMessage(uri.AbsoluteUri, newTab));
    }

    private void Fail(DocumentErrorKind kind, string detail, bool requiresNewWebView = false)
    {
        // A dead browser process is reported even if the page had already failed: only a new WebView can recover.
        if (_failed && !(requiresNewWebView && !_webViewDead))
        {
            return;
        }

        _failed = true;
        _webViewDead |= requiresNewWebView;
        IsReady = false;
        HostFailed?.Invoke(this, new WebViewHostFailedEventArgs(kind, detail, requiresNewWebView));
    }
}
