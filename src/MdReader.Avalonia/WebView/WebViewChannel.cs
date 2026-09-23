using MdReader.Core.Diagnostics;
using MdReader.Core.Paths;
using MdReader.Core.Protocol;
using MdReader.Core.Theming;
using Microsoft.Web.WebView2.Core;

namespace MdReader.Avalonia.WebView;

/// <summary>
/// The phase-1 stand-in for <c>MdReader.App.Documents.IWebViewChannel</c>: virtual host mappings, hardening,
/// origin-checked message intake and posting, for one <see cref="CoreWebView2Controller"/>.
/// </summary>
/// <remarks>
/// Compared with <c>WebViewBridge</c> this drops what the spike doesn't need (render-process crash recovery, drop
/// files, doc-root remapping, protocol-mismatch reporting). What is left is identical apart from the type it is handed:
/// a controller instead of a WPF <c>WebView2</c>. UI thread only.
/// </remarks>
internal sealed class WebViewChannel : IDisposable
{
    private const string Category = "WebViewChannel";

    private readonly CoreWebView2Controller _controller;
    private readonly CoreWebView2 _core;
    private readonly string _webRoot;
    private readonly IAppLog _log;

    private bool _disposed;

    public WebViewChannel(CoreWebView2Controller controller, string webRoot, IAppLog log)
    {
        _controller = controller;
        _core = controller.CoreWebView2;
        _webRoot = webRoot;
        _log = log;
    }

    /// <summary>'ready' has been received for the current page load.</summary>
    public bool IsReady { get; private set; }

    public event EventHandler? Ready;

    /// <summary>Validated web messages other than 'ready'.</summary>
    public event EventHandler<WebMessage>? MessageReceived;

    /// <summary>The viewer page itself failed to load (a shell-level error, not a document error).</summary>
    public event EventHandler<string>? PageFailed;

    public CoreWebView2 Core => _core;

    /// <summary>
    /// Maps the two virtual hosts, hardens the view and navigates to the page. Handlers are registered BEFORE the
    /// navigation (§6), otherwise the page's first messages are lost.
    /// </summary>
    public void Initialize(string resourceRoot, AppTheme theme, Action<Uri, bool> routeLink)
    {
        ArgumentNullException.ThrowIfNull(resourceRoot);
        ArgumentNullException.ThrowIfNull(routeLink);

        WebViewSecurity.Apply(_controller, theme, _log, routeLink);

        // The app host must map or nothing can be shown; the doc host is best effort (§4.3, §8.4).
        _core.SetVirtualHostNameToFolderMapping(ProtocolConstants.AppHost, _webRoot, CoreWebView2HostResourceAccessKind.Deny);
        MapDocumentRoot(resourceRoot);

        _core.WebMessageReceived += OnWebMessageReceived;
        _core.NavigationCompleted += OnNavigationCompleted;

        Navigate(theme);
    }

    public void Navigate(AppTheme theme)
    {
        IsReady = false;
        string themeName = theme == AppTheme.Dark ? "dark" : "light";
        _core.Navigate(ProtocolConstants.PageUrl + "?theme=" + themeName + "&style=colorful");
        _log.Write(AppLogLevel.Info, Category, $"Navigating to the viewer page ({themeName}).");
    }

    public void Post(string json)
    {
        if (_disposed)
        {
            return;
        }

        if (!IsReady)
        {
            _log.Write(AppLogLevel.Debug, Category, $"Dropped a message because the page isn't ready: {Describe(json)}.");
            return;
        }

        try
        {
            _core.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Warning, Category, $"PostWebMessageAsJson failed: {Describe(json)}.", ex);
        }
    }

    public void Post(HostMessage message) => Post(ProtocolSerializer.Serialize(message));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsReady = false;
        try
        {
            _core.WebMessageReceived -= OnWebMessageReceived;
            _core.NavigationCompleted -= OnNavigationCompleted;
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Debug, Category, "Unsubscribing from CoreWebView2 events failed.", ex);
        }
    }

    private void MapDocumentRoot(string root)
    {
        if (root.Length > ResourceRootResolver.MaxMappablePathLength)
        {
            _log.Write(AppLogLevel.Warning, Category, $"Resource root is too long to map ({root.Length} chars); local images are blocked: {root}");
            return;
        }

        try
        {
            _core.SetVirtualHostNameToFolderMapping(ProtocolConstants.DocHost, root, CoreWebView2HostResourceAccessKind.DenyCors);
            _log.Write(AppLogLevel.Info, Category, $"Document resource root mapped: {root}");
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Warning, Category, $"Couldn't map the resource root {root}; local images are blocked.", ex);
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        try
        {
            if (e.IsSuccess && e.HttpStatusCode < 400)
            {
                return;
            }

            if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
            {
                return;
            }

            string detail = $"The viewer page failed to load (status {e.WebErrorStatus}, HTTP {e.HttpStatusCode}). Web root: {_webRoot}";
            _log.Write(AppLogLevel.Error, Category, detail);
            PageFailed?.Invoke(this, detail);
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
                _log.Write(AppLogLevel.Warning, Category, $"Dropped an invalid web message ({WebViewSecurity.ForLog(error, 300)}): {Describe(json)}.");
                return;
            }

            if (message is ReadyMessage ready)
            {
                OnReady(ready);
                return;
            }

            MessageReceived?.Invoke(this, message);
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
            string detail = $"The viewer page speaks protocol {ready.Protocol}; the host requires {ProtocolConstants.Version}.";
            _log.Write(AppLogLevel.Error, Category, detail);
            PageFailed?.Invoke(this, detail);
            return;
        }

        IsReady = true;
        _log.Write(AppLogLevel.Info, Category, "The page is ready.");
        Ready?.Invoke(this, EventArgs.Empty);
    }

    private static string Describe(string? json)
    {
        if (json is null)
        {
            return "(null)";
        }

        const int Max = 120;
        return json.Length <= Max
            ? WebViewSecurity.ForLog(json, Max)
            : $"{WebViewSecurity.ForLog(json[..Max], Max)}… ({json.Length} chars)";
    }
}
