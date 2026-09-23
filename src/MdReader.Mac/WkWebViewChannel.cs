using MdReader.Core.Diagnostics;
using MdReader.Core.Documents;
using MdReader.Core.Hosting;
using MdReader.Core.Paths;
using MdReader.Core.Protocol;
using MdReader.Core.Theming;
using MdReader.Mac.Interop;
using MdReader.Shell.Documents;
using MdReader.Shell.Services;

namespace MdReader.Mac;

/// <summary>
/// <see cref="IWebViewChannel"/> for one WKWebView: the scheme handler that stands in for WebView2's virtual host
/// mappings, the §8.4 hardening, origin-checked message intake, posting, find, print, PDF, capture, zoom and theme,
/// and recovery from a web-content process that died (ARCHITECTURE §4.10, §6, §7.1, §8.4).
/// All members are UI-thread only, because WebKit and AppKit are main-thread only.
/// </summary>
/// <remarks>
/// <para>The shell supplies the view's place in the window through <see cref="IWkWebViewSurface"/>; everything below
/// this line is the same in any shell, which is the same split the Windows backend makes with
/// <c>IWebView2Surface</c>.</para>
/// <para><b>Where WKWebView differs from WebView2</b> — each one is a deliberate decision, not an omission:
/// the two virtual hosts are served over <see cref="ProtocolConstants.MacScheme"/> rather than https, because
/// <c>WKURLSchemeHandler</c> refuses the schemes WebKit implements; find reports only "found / not found", so the
/// bar shows no counter (<see cref="FindSession.CountersKnown"/>); there is no page-zoom gesture, so
/// <see cref="ZoomChanged"/> never fires; and a dropped file is taken from the pasteboard by the view itself,
/// because WebKit gives JavaScript no path for a <c>File</c>.</para>
/// </remarks>
public sealed class WkWebViewChannel : IHostedWebViewChannel, IWkWebViewCallbacks
{
    private const string Category = "WebViewBridge";
    private const int MaxRenderProcessFailures = 3;
    private static readonly TimeSpan RenderProcessFailureWindow = TimeSpan.FromSeconds(60);

    private readonly IWkWebViewSurface _surface;
    private readonly IThemeService _theme;
    private readonly AppPaths _paths;
    private readonly IPerfRecorder _perf;
    private readonly IAppLog _log;
    private readonly TimeProvider _time;
    private readonly IFileSystemProbe _fileSystem;
    private readonly PathPolicy _policy;
    private readonly HostedResourceResolver _resources;
    private readonly Queue<long> _renderProcessFailures = new();
    private readonly HashSet<nint> _liveSchemeTasks = [];

    private nint _handler;                   // the Objective-C delegate object, retained for the channel's life
    private nint _webView;
    private string? _unmappedDocumentRoot;   // doc folder that didn't exist yet
    private FindSession? _findSession;
    private string _findTerm = "";
    private bool _failed;
    private bool _disposed;

    public WkWebViewChannel(IWkWebViewSurface surface, IThemeService theme, AppPaths paths, IPerfRecorder perf, IAppLog log,
                            TimeProvider time, IFileSystemProbe fileSystem, PathPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(theme);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(perf);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(fileSystem);
        _surface = surface;
        _theme = theme;
        _paths = paths;
        _perf = perf;
        _log = log;
        _time = time;
        _fileSystem = fileSystem;
        _policy = policy ?? PathPolicy.Current;
        _resources = new HostedResourceResolver(fileSystem, _policy) { AppRoot = paths.WebRoot };
        _surface.ZoomFactorChanged += OnZoomFactorChanged;
    }

    public bool IsReady { get; private set; }

    public event EventHandler? Ready;

    public event EventHandler<WebMessage>? MessageReceived;

    public event EventHandler<IReadOnlyList<string>>? FilesDropped;

    /// Raised when the page can't be shown (protocol mismatch, the viewer page failed to load, repeated web-content
    /// process crashes). The view replaces the web view with its own error.
    public event EventHandler<WebViewHostFailedEventArgs>? HostFailed;

    /// True when local images can't be served because the resource root couldn't be mapped.
    public bool LocalResourcesUnavailable { get; private set; }

    /// <summary>
    /// Creates the web view, maps the two hosts and navigates to the viewer page. The equivalent of the Windows
    /// backend's <c>InitializeAsync(environment, resourceRoot)</c>.
    /// </summary>
    public async Task InitializeAsync(WkWebViewEnvironment environment, string resourceRoot)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_webView != 0)
        {
            throw new InvalidOperationException("The web view is already initialized.");
        }

        nint handler = WkNative.CreateDelegate(this);
        _handler = handler;

        nint container = await _surface.GetContainerAsync();
        if (_disposed)
        {
            return;
        }

        nint webView = ObjC.WithPool(() =>
        {
            nint configuration = environment.CreateConfiguration(handler);
            CGRect frame = MacNativeView.Bounds(container);
            nint view = WkNative.CreateWebView(configuration, frame, this);
            ObjC.Release(configuration);
            return view;
        });

        _webView = webView;
        _perf.Mark(PerfMarks.WebViewReady);

        ObjC.WithPool(() =>
        {
            // Hardening goes on before the view is visible and before the first navigation (§6, §8.4).
            WkWebViewSecurity.ApplyToWebView(webView, handler, _theme.EffectiveTheme, _log);
            MapDocumentRoot(resourceRoot);
        });

        _surface.AttachWebView(webView);
        NavigateToPage();
    }

    public void Post(string json)
    {
        if (_disposed)
        {
            return;
        }

        if (!IsReady || _webView == 0)
        {
            _log.Write(AppLogLevel.Debug, Category, $"Dropped a message because the page isn't ready: {WkWebViewSecurity.Describe(json)}.");
            return;
        }

        try
        {
            Evaluate(WebMessageIntake.DeliveryScript(json), null);
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Warning, Category, $"Delivering a host message failed: {WkWebViewSecurity.Describe(json)}.", ex);
        }
    }

    /// <summary>
    /// Retries the document-host mapping if the document's folder didn't exist at initialization. Called before a
    /// render payload is posted, so a folder created later (file restored) still gets its images.
    /// </summary>
    public void EnsureResourceRootMapped()
    {
        if (_unmappedDocumentRoot is { } root && !_disposed)
        {
            ObjC.WithPool(() => MapDocumentRoot(root));
        }
    }

    // ----- Find -----

    /// <summary>
    /// Starts (or restarts) a find session. WebKit's public find API answers "found" or "not found" and nothing
    /// else, so the session it returns reports <see cref="FindSession.CountersKnown"/> = false and the bar shows no
    /// "3/18". Everything else — the debounce, next/previous, Esc — behaves as it does on Windows.
    /// </summary>
    public Task<FindSession?> StartFindAsync(string term)
    {
        ArgumentNullException.ThrowIfNull(term);
        if (_disposed || _webView == 0)
        {
            return Task.FromResult<FindSession?>(null);
        }

        RequireFindApi();
        FindSession session = _findSession ??= FindSession.WithoutCounters();
        _findTerm = term;
        ClearSelection();
        if (term.Length == 0)
        {
            session.ReportMatch(found: false);
            return Task.FromResult<FindSession?>(session);
        }

        var completion = new TaskCompletionSource<FindSession?>(TaskCreationOptions.RunContinuationsAsynchronously);
        Find(term, backwards: false, found =>
        {
            session.ReportMatch(found);
            completion.TrySetResult(session);
        });
        return completion.Task;
    }

    public void FindNext() => FindAgain(backwards: false);

    public void FindPrevious() => FindAgain(backwards: true);

    /// Ends the find session and clears its highlight. Safe to call at any time; never throws.
    public void StopFind()
    {
        _findTerm = "";
        try
        {
            ClearSelection();
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Debug, Category, "Clearing the find selection failed.", ex);
        }

        _findSession?.ReportMatch(found: false);
    }

    // ----- Print, export, capture -----

    public Task ShowPrintUiAsync()
    {
        nint webView = RequireWebView();
        ObjC.WithPool(() =>
        {
            nint printInfo = ObjC.Send(ObjC.RequireClass("NSPrintInfo"), ObjC.Selector("sharedPrintInfo"));
            nint operation = ObjC.Send(webView, ObjC.Selector("printOperationWithPrintInfo:"), printInfo);
            if (operation == 0)
            {
                throw new InvalidOperationException("This macOS version can't print from a web view.");
            }

            ObjC.SendVoidBool(operation, ObjC.Selector("setShowsPrintPanel:"), 1);
            ObjC.SendVoidBool(operation, ObjC.Selector("setShowsProgressPanel:"), 1);
            nint window = ObjC.Send(webView, ObjC.Selector("window"));

            // The sheet runs itself; like ShowPrintUI on Windows, this completes once the dialog has been asked for.
            ObjC.Send(operation, ObjC.Selector("runOperationModalForWindow:delegate:didRunSelector:contextInfo:"),
                      window, 0, 0, 0);
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Prints to a PDF with backgrounds and without headers or footers. <c>createPDFWithConfiguration:</c> renders
    /// the page's own content rather than a paginated print job, so there is nowhere for a header to come from and
    /// the document's backgrounds are part of the render.
    /// </summary>
    public Task<bool> PrintToPdfAsync(string pdfPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(pdfPath);
        nint webView = RequireWebView();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ObjC.WithPool(() =>
        {
            nint configuration = ObjC.New(ObjC.RequireClass("WKPDFConfiguration"));
            nint block = Blocks.ResultAndError((data, error) => ObjC.WithPool(() =>
            {
                if (data == 0)
                {
                    _log.Write(AppLogLevel.Warning, Category, $"Creating the PDF failed: {Foundation.ErrorDescription(error)}");
                    completion.TrySetResult(false);
                    return;
                }

                bool written = Foundation.WriteData(data, pdfPath, out string? writeError);
                if (!written)
                {
                    _log.Write(AppLogLevel.Warning, Category, $"Writing the PDF failed: {writeError}");
                }

                completion.TrySetResult(written);
            }));
            ObjC.SendVoid(webView, ObjC.Selector("createPDFWithConfiguration:completionHandler:"), configuration, block);
            ObjC.Release(configuration);
        });

        return completion.Task;
    }

    /// <summary>Writes a PNG of what the web view is showing.</summary>
    public Task CapturePreviewAsync(Stream pngDestination)
    {
        ArgumentNullException.ThrowIfNull(pngDestination);
        nint webView = RequireWebView();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ObjC.WithPool(() =>
        {
            nint configuration = ObjC.New(ObjC.RequireClass("WKSnapshotConfiguration"));
            nint block = Blocks.ResultAndError((image, error) => ObjC.WithPool(() =>
            {
                try
                {
                    if (image == 0)
                    {
                        throw new InvalidOperationException(Foundation.ErrorDescription(error) ?? "The snapshot failed.");
                    }

                    byte[] png = ToPng(image);
                    pngDestination.Write(png, 0, png.Length);
                    completion.TrySetResult();
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }));
            ObjC.SendVoid(webView, ObjC.Selector("takeSnapshotWithConfiguration:completionHandler:"), configuration, block);
            ObjC.Release(configuration);
        });

        return completion.Task;
    }

    // ----- Zoom -----

    public double Zoom => _surface.ZoomFactor;

    public void SetZoom(double zoomFactor) => _surface.ZoomFactor = zoomFactor;

    public event EventHandler? ZoomChanged;

    // ----- Theme -----

    public void SetBackgroundColor(byte red, byte green, byte blue)
    {
        try
        {
            _surface.SetDefaultBackgroundColor(red, green, blue);
            if (_webView is var webView and not 0 && !_disposed)
            {
                ObjC.WithPool(() => WkWebViewSecurity.ApplyBackgroundColor(webView, red, green, blue, _log));
            }
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Warning, Category, "Couldn't apply the theme to the web view.", ex);
        }
    }

    public void SetPreferredColorScheme(AppTheme theme)
    {
        if (_webView is var webView and not 0 && !_disposed)
        {
            ObjC.WithPool(() => WkWebViewSecurity.ApplyAppearance(webView, theme, _log));
        }
    }

    // ----- Lifetime -----

    /// <summary>
    /// True when <see cref="Restart"/> can recover. A WKWebView survives its web-content process, so unlike WebView2
    /// there is no state in which only a brand-new view can help: this is false only after disposal.
    /// </summary>
    public bool CanRestart => !_disposed && _webView != 0;

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
        _findSession = null;
        _surface.ZoomFactorChanged -= OnZoomFactorChanged;

        nint webView = _webView;
        nint handler = _handler;
        _webView = 0;
        _handler = 0;
        try
        {
            ObjC.WithPool(() =>
            {
                if (webView != 0)
                {
                    // The delegates and the message handler hold the delegate object; drop them before releasing it.
                    ObjC.SendVoid(webView, ObjC.Selector("setNavigationDelegate:"), 0);
                    ObjC.SendVoid(webView, ObjC.Selector("setUIDelegate:"), 0);
                    nint configuration = ObjC.Send(webView, ObjC.Selector("configuration"));
                    nint controller = configuration == 0 ? 0 : ObjC.Send(configuration, ObjC.Selector("userContentController"));
                    if (controller != 0)
                    {
                        ObjC.SendVoid(controller, ObjC.Selector("removeScriptMessageHandlerForName:"),
                                      Foundation.String(WebKitConstants.ScriptMessageHandlerName));
                    }

                    WkNative.Forget(webView);
                }

                if (handler != 0)
                {
                    WkNative.Forget(handler);
                    ObjC.Release(handler);
                }
            });
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Debug, Category, "Detaching the web view's delegates failed.", ex);
        }
    }

    // ----------------------------------------------------------------------------------------------------------------
    // The Objective-C delegate's calls (IWkWebViewCallbacks). Every one of them runs on the main thread, inside
    // WebKit's own stack, so they do the least possible work and never re-enter the web view synchronously.

    void IWkWebViewCallbacks.OnNavigationAction(nint navigationAction, nint decisionHandler)
    {
        nint policy = WebKitConstants.NavigationActionPolicyCancel;
        try
        {
            nint request = ObjC.Send(navigationAction, ObjC.Selector("request"));
            string? target = Foundation.UrlAbsoluteString(ObjC.Send(request, ObjC.Selector("URL")));
            nint targetFrame = ObjC.Send(navigationAction, ObjC.Selector("targetFrame"));
            bool isMainFrame = targetFrame == 0 || ObjC.SendBool(targetFrame, ObjC.Selector("isMainFrame")) != 0;
            var type = (NavigationPolicy.NavigationType)ObjC.SendLong(navigationAction, ObjC.Selector("navigationType"));

            NavigationDecision decision = NavigationPolicy.ForNavigation(target, type, isMainFrame, NewTabRequested(navigationAction));
            switch (decision.Outcome)
            {
                case NavigationOutcome.Allow:
                    policy = WebKitConstants.NavigationActionPolicyAllow;
                    IsReady = false;   // a (re)load of our page: nothing may be posted until its 'ready'
                    break;

                case NavigationOutcome.CancelAndRouteLink:
                    _log.Write(AppLogLevel.Info, Category, $"Blocked navigation to {WkWebViewSecurity.Shorten(target)} ({type}).");
                    RouteLink(decision.Link!, decision.NewTab);
                    break;

                default:
                    _log.Write(AppLogLevel.Info, Category, $"Blocked navigation to {WkWebViewSecurity.Shorten(target)} ({type}).");
                    break;
            }
        }
        finally
        {
            Blocks.Call(decisionHandler, policy);
        }
    }

    void IWkWebViewCallbacks.OnNavigationResponse(nint navigationResponse, nint decisionHandler)
    {
        nint policy = WebKitConstants.NavigationResponsePolicyCancel;
        try
        {
            nint response = ObjC.Send(navigationResponse, ObjC.Selector("response"));
            string? url = Foundation.UrlAbsoluteString(ObjC.Send(response, ObjC.Selector("URL")));
            bool canShow = ObjC.SendBool(navigationResponse, ObjC.Selector("canShowMIMEType")) != 0;
            if (NavigationPolicy.AllowResponse(url, canShow))
            {
                policy = WebKitConstants.NavigationResponsePolicyAllow;
            }
            else
            {
                // Anything WebKit can't display becomes a download if it is allowed through; this is the §8.4
                // "DownloadStarting → Cancel" row, one step earlier.
                _log.Write(AppLogLevel.Info, Category, $"Blocked a response for {WkWebViewSecurity.Shorten(url)}.");
            }
        }
        finally
        {
            Blocks.Call(decisionHandler, policy);
        }
    }

    void IWkWebViewCallbacks.OnNavigationFinished()
    {
        // The page reports 'ready' itself; this only tells us the load didn't fail.
    }

    void IWkWebViewCallbacks.OnNavigationFailed(string? error)
    {
        _log.Write(AppLogLevel.Error, Category, $"The viewer page failed to load: {WkWebViewSecurity.ForLog(error, 300)}. Web root: {_paths.WebRoot}");
        Fail(DocumentErrorKind.RenderFailed, $"The viewer page failed to load ({error}).");
    }

    void IWkWebViewCallbacks.OnWebContentProcessTerminated()
    {
        IsReady = false;
        _log.Write(AppLogLevel.Warning, Category, "The page's web content process ended.");
        long now = _time.GetTimestamp();
        _renderProcessFailures.Enqueue(now);
        while (_renderProcessFailures.Count > 0 && _time.GetElapsedTime(_renderProcessFailures.Peek(), now) > RenderProcessFailureWindow)
        {
            _renderProcessFailures.Dequeue();
        }

        if (_renderProcessFailures.Count >= MaxRenderProcessFailures)
        {
            Fail(DocumentErrorKind.RenderFailed, $"The page crashed {_renderProcessFailures.Count} times within a minute.");
        }
        else
        {
            NavigateToPage();   // state is re-posted on the next 'ready'
        }
    }

    void IWkWebViewCallbacks.OnAuthenticationChallenge(nint challenge)
    {
        nint space = ObjC.Send(challenge, ObjC.Selector("protectionSpace"));
        string? host = space == 0 ? null : Foundation.ToManagedString(ObjC.Send(space, ObjC.Selector("host")));
        _log.Write(AppLogLevel.Info, Category, $"Cancelled an authentication challenge from {WkWebViewSecurity.Shorten(host)}.");
    }

    void IWkWebViewCallbacks.OnDownloadStarted(nint download)
    {
        // Unreachable while the policy above never answers "download", which is why it only has to say so.
        _log.Write(AppLogLevel.Warning, Category, "A download started and was cancelled.");
        if (download != 0)
        {
            ObjC.SendVoid(download, ObjC.Selector("cancel:"), 0);
        }
    }

    void IWkWebViewCallbacks.OnNewWindow(nint navigationAction)
    {
        nint request = ObjC.Send(navigationAction, ObjC.Selector("request"));
        string? target = Foundation.UrlAbsoluteString(ObjC.Send(request, ObjC.Selector("URL")));
        var type = (NavigationPolicy.NavigationType)ObjC.SendLong(navigationAction, ObjC.Selector("navigationType"));
        _log.Write(AppLogLevel.Info, Category, $"Blocked a new window for {WkWebViewSecurity.Shorten(target)} ({type}).");

        NavigationDecision decision = NavigationPolicy.ForNewWindow(target, type);
        if (decision.Outcome == NavigationOutcome.CancelAndRouteLink)
        {
            RouteLink(decision.Link!, decision.NewTab);
        }
    }

    void IWkWebViewCallbacks.OnScriptDialogSuppressed(string kind) =>
        _log.Write(AppLogLevel.Info, Category, $"Suppressed a script {kind} dialog.");

    void IWkWebViewCallbacks.OnPermissionDenied(string what) =>
        _log.Write(AppLogLevel.Info, Category, $"Denied permission: {what}.");

    void IWkWebViewCallbacks.OnScriptMessage(nint message)
    {
        nint frame = ObjC.Send(message, ObjC.Selector("frameInfo"));
        bool isMainFrame = frame != 0 && ObjC.SendBool(frame, ObjC.Selector("isMainFrame")) != 0;
        nint request = frame == 0 ? 0 : ObjC.Send(frame, ObjC.Selector("request"));
        string? frameUrl = request == 0 ? null : Foundation.UrlAbsoluteString(ObjC.Send(request, ObjC.Selector("URL")));

        // The page sends JSON text on this transport (bridge.js), so a body that isn't a string is a rejection.
        nint body = ObjC.Send(message, ObjC.Selector("body"));
        string? json = body != 0 && ObjC.SendBool(body, ObjC.Selector("isKindOfClass:"), ObjC.RequireClass("NSString")) != 0
            ? Foundation.ToManagedString(body)
            : null;

        WebMessageIntakeResult result = WebMessageIntake.Classify(frameUrl, isMainFrame, json);
        switch (result.Kind)
        {
            case WebMessageKind.Ready:
                OnReady((ReadyMessage)result.Message!);
                break;

            case WebMessageKind.Drop:
                // Files never arrive this way on macOS: the view takes them off the pasteboard instead.
                _log.Write(AppLogLevel.Debug, Category, "The page reported a drop; macOS takes dropped files from the view.");
                break;

            case WebMessageKind.Message:
                MessageReceived?.Invoke(this, result.Message!);
                break;

            default:
                _log.Write(AppLogLevel.Warning, Category,
                    $"Dropped a web message ({WkWebViewSecurity.ForLog(result.Error, 300)}): {WkWebViewSecurity.Describe(json)}.");
                break;
        }
    }

    void IWkWebViewCallbacks.OnSchemeTaskStarted(nint task)
    {
        _liveSchemeTasks.Add(task);
        nint request = ObjC.Send(task, ObjC.Selector("request"));
        nint url = ObjC.Send(request, ObjC.Selector("URL"));
        string? absolute = Foundation.UrlAbsoluteString(url);

        ResourceRefusal refusal = _resources.Resolve(absolute, out HostedResource resource);
        if (refusal != ResourceRefusal.None)
        {
            // NotFound is ordinary (a README that points at a missing image); the rest is a rule doing its job.
            _log.Write(refusal == ResourceRefusal.NotFound ? AppLogLevel.Debug : AppLogLevel.Info, Category,
                $"Refused {WkWebViewSecurity.Shorten(absolute)}: {refusal}.");
            FailTask(task, url, refusal);
            return;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(resource.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.Write(AppLogLevel.Info, Category, $"Couldn't read {WkWebViewSecurity.Shorten(absolute)}: {ex.Message}");
            FailTask(task, url, ResourceRefusal.NotFound);
            return;
        }

        if (!_liveSchemeTasks.Contains(task))
        {
            return;   // stopped while the file was being read
        }

        nint headers = Foundation.Dictionary(
            [Foundation.String("Content-Type"), Foundation.String("Content-Length"), Foundation.String("X-Content-Type-Options")],
            [Foundation.String(resource.MediaType), Foundation.String(bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)), Foundation.String("nosniff")]);
        nint response = ObjC.Send(ObjC.Send(ObjC.RequireClass("NSHTTPURLResponse"), ObjC.Selector("alloc")),
                                  ObjC.Selector("initWithURL:statusCode:HTTPVersion:headerFields:"),
                                  url, 200, Foundation.String("HTTP/1.1"), headers);
        try
        {
            ObjC.SendVoid(task, ObjC.Selector("didReceiveResponse:"), response);
            ObjC.SendVoid(task, ObjC.Selector("didReceiveData:"), Foundation.Data(bytes));
            ObjC.SendVoid(task, ObjC.Selector("didFinish"));
        }
        finally
        {
            ObjC.Release(response);
            _liveSchemeTasks.Remove(task);
        }
    }

    void IWkWebViewCallbacks.OnSchemeTaskStopped(nint task) => _liveSchemeTasks.Remove(task);

    void IWkWebViewCallbacks.OnFilesDropped(IReadOnlyList<string> paths)
    {
        _log.Write(AppLogLevel.Info, Category, $"Files dropped on the page: {paths.Count}.");
        if (paths.Count > 0)
        {
            FilesDropped?.Invoke(this, paths);
        }
    }

    void IWkWebViewCallbacks.OnDelegateFailed(string name, Exception exception) =>
        _log.Write(AppLogLevel.Error, Category, $"The {name} delegate failed; the request took the closed outcome.", exception);

    // ----------------------------------------------------------------------------------------------------------------

    private void OnZoomFactorChanged(object? sender, EventArgs e)
    {
        if (!_disposed)
        {
            ZoomChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Cmd-click and middle-click mean "new tab", the same three modifiers the page's own link handler reports.
    /// <c>modifierFlags</c> and <c>buttonNumber</c> are macOS-only properties of <c>WKNavigationAction</c>.
    /// </summary>
    private static bool NewTabRequested(nint navigationAction)
    {
        if (!ObjC.RespondsTo(navigationAction, "modifierFlags"))
        {
            return false;
        }

        nint flags = (nint)ObjC.SendUInt(navigationAction, ObjC.Selector("modifierFlags"));
        nint button = (nint)ObjC.SendLong(navigationAction, ObjC.Selector("buttonNumber"));
        return (flags & (WebKitConstants.EventModifierFlagCommand | WebKitConstants.EventModifierFlagControl)) != 0
               || button == WebKitConstants.MiddleButtonNumber;
    }

    private void RouteLink(Uri uri, bool newTab)
    {
        if (!_disposed)
        {
            // Routed exactly like a clicked link, so the LinkClassifier rules (UNC, blocked hosts, …) apply.
            MessageReceived?.Invoke(this, new LinkMessage(uri.AbsoluteUri, newTab));
        }
    }

    private void MapDocumentRoot(string root)
    {
        _unmappedDocumentRoot = null;
        if (root.Length > _policy.MaxMappableRootLength)
        {
            LocalResourcesUnavailable = true;
            _resources.DocumentRoot = null;
            _log.Write(AppLogLevel.Warning, Category, $"Resource root is too long to map ({root.Length} chars); local images are blocked: {root}");
            return;
        }

        string? normalized = _policy.NormalizeFullPath(root);
        if (normalized is null)
        {
            LocalResourcesUnavailable = true;
            _resources.DocumentRoot = null;
            _log.Write(AppLogLevel.Warning, Category, $"Couldn't map the resource root {root}; local images are blocked.");
            return;
        }

        if (!_fileSystem.DirectoryExists(normalized))
        {
            // The document's folder is missing: the load reports NotFound and the page shows that error. The mapping
            // is retried from EnsureResourceRootMapped before the next payload.
            _unmappedDocumentRoot = root;
            _resources.DocumentRoot = null;
            _log.Write(AppLogLevel.Info, Category, $"Resource root doesn't exist (yet): {root}.");
            return;
        }

        _resources.DocumentRoot = normalized;
        LocalResourcesUnavailable = false;
    }

    private void NavigateToPage()
    {
        if (_webView == 0 || _disposed)
        {
            return;
        }

        IsReady = false;
        string theme = _theme.EffectiveTheme == AppTheme.Dark ? "dark" : "light";
        try
        {
            ObjC.WithPool(() =>
            {
                nint url = Foundation.Url(ProtocolConstants.PageUrl + "?theme=" + theme);
                nint request = ObjC.Send(ObjC.RequireClass("NSURLRequest"), ObjC.Selector("requestWithURL:"), url);
                ObjC.Send(_webView, ObjC.Selector("loadRequest:"), request);
            });
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, Category, "Navigating to the viewer page failed.", ex);
            Fail(DocumentErrorKind.RenderFailed, "The viewer page couldn't be opened. " + ex.Message);
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

    private void Fail(DocumentErrorKind kind, string detail)
    {
        if (_failed)
        {
            return;
        }

        _failed = true;
        IsReady = false;
        HostFailed?.Invoke(this, new WebViewHostFailedEventArgs(kind, detail, requiresNewWebView: false));
    }

    private nint RequireWebView() =>
        _disposed || _webView == 0 ? throw new InvalidOperationException("The document's web view isn't ready.") : _webView;

    private void RequireFindApi()
    {
        if (_webView != 0 && !ObjC.RespondsTo(_webView, "findString:withConfiguration:completionHandler:"))
        {
            throw new FindNotSupportedException("This version of macOS has no WKWebView find API (macOS 13 or later is needed).");
        }
    }

    private void FindAgain(bool backwards)
    {
        if (_disposed || _webView == 0 || _findTerm.Length == 0 || _findSession is not { } session)
        {
            return;
        }

        RequireFindApi();
        Find(_findTerm, backwards, session.ReportMatch);
    }

    private void Find(string term, bool backwards, Action<bool> onResult) => ObjC.WithPool(() =>
    {
        nint configuration = ObjC.New(ObjC.RequireClass("WKFindConfiguration"));
        ObjC.SendVoidBool(configuration, ObjC.Selector("setBackwards:"), (byte)(backwards ? 1 : 0));
        ObjC.SendVoidBool(configuration, ObjC.Selector("setCaseSensitive:"), 0);
        ObjC.SendVoidBool(configuration, ObjC.Selector("setWraps:"), 1);
        nint block = Blocks.SingleResult(result =>
        {
            bool found = result != 0 && ObjC.SendBool(result, ObjC.Selector("matchFound")) != 0;
            onResult(found);
        });
        ObjC.SendVoid(_webView, ObjC.Selector("findString:withConfiguration:completionHandler:"),
                      Foundation.String(term), configuration, block);
        ObjC.Release(configuration);
    });

    /// <summary>
    /// WebKit highlights a find hit by selecting it, and its public API has no "stop finding". Clearing the selection
    /// is what clears the highlight, and it is the one piece of script the host runs that isn't a host message.
    /// </summary>
    private void ClearSelection()
    {
        if (_webView != 0 && !_disposed)
        {
            Evaluate("(function(){var s=window.getSelection();if(s){s.removeAllRanges();}})()", null);
        }
    }

    private void Evaluate(string script, Action<nint, nint>? completion) => ObjC.WithPool(() =>
    {
        nint block = completion is null ? 0 : Blocks.ResultAndError(completion);
        ObjC.SendVoid(_webView, ObjC.Selector("evaluateJavaScript:completionHandler:"), Foundation.String(script), block);
    });

    private void FailTask(nint task, nint url, ResourceRefusal refusal)
    {
        _liveSchemeTasks.Remove(task);

        // NSURLErrorFileDoesNotExist / NSURLErrorNoPermissionsToReadFile / NSURLErrorUnsupportedURL: the page sees a
        // failed load, exactly as it would for a virtual host mapping that refused on Windows.
        nint code = refusal switch
        {
            ResourceRefusal.NotFound or ResourceRefusal.NotMapped => -1100,
            ResourceRefusal.Forbidden or ResourceRefusal.OutsideRoot or ResourceRefusal.BadPath => -1102,
            _ => -1002,
        };
        nint error = ObjC.Send(ObjC.RequireClass("NSError"), ObjC.Selector("errorWithDomain:code:userInfo:"),
                               Foundation.String("NSURLErrorDomain"), code, 0);
        ObjC.SendVoid(task, ObjC.Selector("didFailWithError:"), error);
        _ = url;
    }

    /// <summary>An NSImage as PNG bytes, through its bitmap representation.</summary>
    private static byte[] ToPng(nint image)
    {
        nint tiff = ObjC.Send(image, ObjC.Selector("TIFFRepresentation"));
        nint representation = ObjC.Send(ObjC.RequireClass("NSBitmapImageRep"), ObjC.Selector("imageRepWithData:"), tiff);
        if (representation == 0)
        {
            throw new InvalidOperationException("The snapshot couldn't be converted to a bitmap.");
        }

        const nint NSBitmapImageFileTypePng = 4;
        nint data = ObjC.Send(representation, ObjC.Selector("representationUsingType:properties:"),
                              NSBitmapImageFileTypePng, Foundation.Dictionary([], []));
        return Foundation.ToManagedBytes(data) ?? throw new InvalidOperationException("The snapshot produced no PNG data.");
    }
}
