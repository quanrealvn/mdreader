using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Threading;
using MdReader.App.Services;
using MdReader.App.ViewModels;
using MdReader.Core.Diagnostics;
using MdReader.Core.Documents;
using MdReader.Core.Paths;
using MdReader.Core.Protocol;
using MdReader.Core.Rendering;
using MdReader.Core.Settings;
using Microsoft.Web.WebView2.Core;
using IOPath = System.IO.Path;

namespace MdReader.App.Documents;

/// <summary>What <see cref="DocumentOpener"/> passes explicitly when it creates a session through ActivatorUtilities.</summary>
internal sealed record DocumentStartInfo(string Path, int DocId, string? Fragment);

/// <summary>Raised on the UI thread after every completed (not cancelled, not superseded) load.</summary>
internal sealed class DocumentLoadCompletedEventArgs(DocumentLoadError? error, bool isFirstAttempt) : EventArgs
{
    /// null = the file was read successfully (even if rendering it then failed).
    public DocumentLoadError? Error { get; } = error;
    public bool IsFirstAttempt { get; } = isFirstAttempt;
}

/// <summary>
/// Per-tab coordinator (ARCHITECTURE §4.10, §5, §7.1, §9): owns the load → render → serialize pipeline and its
/// versioning, the file watcher, the current payload, the banner state, and the handling of every web message.
/// Every public member is UI-thread only; the pipeline itself runs on the thread pool.
/// </summary>
internal sealed class DocumentSession : IDisposable
{
    private const string Category = "DocumentSession";
    private const string WebCategory = "Web";
    private const int MaxWebLogMessagesPerSecond = 20;
    private const int MaxLoggedMessageChars = 2000;
    private static readonly TimeSpan PrintModeTimeout = TimeSpan.FromSeconds(3);

    /// Focus/activation churn while the print dialog opens and generates its preview (≈1.4 s measured for a Mermaid
    /// document in Debug) must not count as "the user came back" (R6).
    private static readonly TimeSpan PrintFallbackGrace = TimeSpan.FromMilliseconds(1500);

    // All banner wording comes from DocumentErrorMessages (ARCHITECTURE §4.4).
    private static readonly BannerInfo DeletedBanner = DocumentErrorMessages.DeletedBanner();
    private static readonly BannerInfo LocalResourcesBanner = DocumentErrorMessages.ResourceRootTooLongBanner();

    private readonly IDocumentLoader _loader;
    private readonly IMarkdownRenderer _renderer;
    private readonly ResourceRootResolver _rootResolver;
    private readonly LinkClassifier _linkClassifier;
    private readonly IDocumentWatcherFactory _watcherFactory;
    private readonly SettingsCoordinator _settings;
    private readonly IThemeService _theme;
    private readonly IDocumentOpener _opener;
    private readonly IExternalLauncher _launcher;
    private readonly IStatusNotifier _status;
    private readonly IClipboardService _clipboard;
    private readonly IPerfRecorder _perf;
    private readonly IAppLog _log;
    private readonly TimeProvider _time;
    private readonly Dispatcher _dispatcher;

    private readonly SemaphoreSlim _pipelineGate = new(1, 1);
    private readonly object _watcherLock = new();
    private readonly List<(RenderPhase Phase, TaskCompletionSource Completion)> _renderWaiters = [];

    private IWebViewChannel? _channel;
    private IDocumentWatcher? _watcher;
    private CancellationTokenSource _pipelineCts = new();
    private int _version;

    private Payload? _payload;               // what the page must show: render parts or one error message
    private bool _payloadPosted;             // _payload fully posted to the current page
    private int _postedCompleteVersion;      // render version whose parts ALL reached the current page (0 = none)
    private int _postGeneration;             // bumps on every post start; an older post loop stops at its next yield
    private BannerInfo? _postedBanner;       // banner the page currently shows (for render payloads)
    private LoadedSnapshot? _lastLoaded;     // last successfully rendered text (rule 4)
    private PerfDocumentInfo? _perfInfo;     // timings of the current render payload
    private int _contentVersion;
    private string? _pendingFragment;        // fragment to scroll to once content exists
    private bool _hadOutcome;

    private bool _deleted;
    private BannerInfo? _reloadBanner;       // a live-reload failure while content stays visible
    private BannerInfo? _encodingBanner;
    private bool? _postedTocVisible;
    private ReadingStyle? _postedReadingStyle;
    private DocumentErrorKind? _hostErrorKind;

    private (bool Enabled, TaskCompletionSource Completion)? _printModeWaiter;
    private long? _printFallbackArmedAt;     // R6: print dialog shown; leave print mode when the user comes back

    private long _logWindowStart;
    private int _logWindowCount;
    private int _logSuppressed;

    private bool _disposed;

    public DocumentSession(
        DocumentStartInfo start,
        IDocumentLoader loader,
        IMarkdownRenderer renderer,
        ResourceRootResolver rootResolver,
        LinkClassifier linkClassifier,
        IDocumentWatcherFactory watcherFactory,
        SettingsCoordinator settings,
        IThemeService theme,
        IDocumentOpener opener,
        IExternalLauncher launcher,
        IStatusNotifier status,
        IClipboardService clipboard,
        IPerfRecorder perf,
        IAppLog log,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(start);
        _loader = loader;
        _renderer = renderer;
        _rootResolver = rootResolver;
        _linkClassifier = linkClassifier;
        _watcherFactory = watcherFactory;
        _settings = settings;
        _theme = theme;
        _opener = opener;
        _launcher = launcher;
        _status = status;
        _clipboard = clipboard;
        _perf = perf;
        _log = log;
        _time = time;
        _dispatcher = Dispatcher.CurrentDispatcher;

        Path = start.Path;
        DocId = start.DocId;
        _pendingFragment = string.IsNullOrEmpty(start.Fragment) ? null : start.Fragment;

        ResourceRootTask = Task.Run(ResolveResourceRoot);
        _theme.EffectiveThemeChanged += OnEffectiveThemeChanged;
        _settings.Changed += OnSettingsChanged;
        StartWatcher();

        // Load + render starts now, before the WebView exists (§4.11 opening flow).
        _ = ReloadCoreAsync(preserveScroll: false, scrollToId: null, force: true);
    }

    public int DocId { get; }

    public string Path { get; }

    /// Rendered title; "" until the first render payload exists (and while an error is shown).
    public string Title { get; private set; } = "";

    public DocumentSessionState State { get; private set; } = DocumentSessionState.Loading;

    public DocumentErrorKind? ErrorKind { get; private set; }

    public bool IsDeleted => _deleted;

    /// Latest payload version for which the page reported <c>rendered{phase:"enhanced"}</c>; 0 = none yet.
    public int RenderedVersion { get; private set; }

    /// Raised on the UI thread whenever Title, State, ErrorKind, IsDeleted or RenderedVersion may have changed.
    public event EventHandler? StateChanged;

    internal event EventHandler<DocumentLoadCompletedEventArgs>? LoadCompleted;

    /// Resource root (nearest .git ancestor, else the document folder), computed on the pool at construction.
    public Task<string> ResourceRootTask { get; }

    internal IWebViewChannel? Channel => _channel;

    public void Attach(IWebViewChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _dispatcher.VerifyAccess();
        if (_disposed)
        {
            return;
        }

        if (_channel is not null)
        {
            throw new InvalidOperationException("The session is already attached to a WebView.");
        }

        _channel = channel;
        channel.Ready += OnChannelReady;
        channel.MessageReceived += OnChannelMessage;
        channel.FilesDropped += OnFilesDropped;
        if (channel.IsReady)
        {
            OnChannelReady(channel, EventArgs.Empty);
        }
    }

    /// The view is replacing its WebView2 (recovery after a failed initialization or a dead browser process): forget
    /// the old channel; the payload is posted again when the new one reports 'ready' after <see cref="Attach"/>.
    internal void Detach(IWebViewChannel channel)
    {
        _dispatcher.VerifyAccess();
        if (!ReferenceEquals(_channel, channel))
        {
            return;
        }

        channel.Ready -= OnChannelReady;
        channel.MessageReceived -= OnChannelMessage;
        channel.FilesDropped -= OnFilesDropped;
        _channel = null;
        _postGeneration++;   // stops a post loop that is still running for the old page
        ResetPageState();
        _printModeWaiter?.Completion.TrySetResult();
        _printModeWaiter = null;
    }

    /// Something the user did after a print dialog was opened (WebView focus, window activation, key input). R6
    /// fallback for a page that doesn't get 'afterprint' after ShowPrintUI(Browser): leave print mode. Idempotent — the
    /// page ignores printMode{false} when it already left print mode on 'afterprint'.
    internal void OnUserInteractionAfterPrint()
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _printFallbackArmedAt is not { } armedAt || _time.GetElapsedTime(armedAt) < PrintFallbackGrace)
        {
            return;
        }

        _printFallbackArmedAt = null;
        if (Post(new PrintModeMessage(false)))
        {
            _log.Write(AppLogLevel.Debug, Category, "Left print mode after the print dialog (R6 fallback).");
        }
    }

    internal bool IsPrintFallbackArmed => _printFallbackArmedAt is not null;

    /// User-initiated reload (F5, "Try again"): always re-renders, even if the text didn't change.
    public Task ReloadAsync(bool preserveScroll, string? scrollToId = null)
    {
        _dispatcher.VerifyAccess();
        return ReloadCoreAsync(preserveScroll, scrollToId, force: true);
    }

    public void ScrollTo(string fragment)
    {
        _dispatcher.VerifyAccess();
        if (_disposed || fragment is null)
        {
            return;
        }

        if (_payload is { IsError: false } && _payloadPosted && Post(new ScrollToMessage(fragment)))
        {
            return;
        }

        // No content on the page yet: scroll once the next render payload has been posted.
        _pendingFragment = fragment;
    }

    /// Ctrl+B / the TOC button (final-review S1): the page decides what "toggle" means — its drawer below 900 CSS px,
    /// otherwise the docked sidebar (reported back as tocVisibilityChanged). No page yet → nothing to toggle.
    public void ToggleToc()
    {
        _dispatcher.VerifyAccess();
        if (!Post(new TocToggleMessage()))
        {
            _log.Write(AppLogLevel.Debug, Category, "TOC toggle ignored: the page isn't ready.");
        }
    }

    public Task WhenRenderedAsync(RenderPhase phase, TimeSpan timeout)
    {
        _dispatcher.VerifyAccess();
        if (_disposed)
        {
            return Task.FromException(new ObjectDisposedException(nameof(DocumentSession)));
        }

        if (State == DocumentSessionState.Error)
        {
            return Task.FromException(CreateRenderFailedException());
        }

        if (IsRendered(phase))
        {
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _renderWaiters.Add((phase, completion));
        return WaitForRenderAsync(completion, timeout);
    }

    public async Task PrintAsync()
    {
        _dispatcher.VerifyAccess();
        CoreWebView2? core = ReadyCore();
        if (core is null)
        {
            _status.ShowStatus("The document isn't ready to print yet.");
            return;
        }

        await SetPrintModeAsync(true);
        if (_disposed)
        {
            return;
        }

        try
        {
            core.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
            // The page leaves print mode itself on 'afterprint'; if that never fires, the next user interaction does (R6).
            _printFallbackArmedAt = _time.GetTimestamp();
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Warning, Category, "ShowPrintUI failed.", ex);
            _status.ShowStatus("Couldn't open the print dialog.");
            Post(new PrintModeMessage(false));
        }
    }

    public async Task<bool> ExportPdfAsync(string pdfPath)
    {
        _dispatcher.VerifyAccess();
        CoreWebView2? core = ReadyCore();
        if (core is null)
        {
            _status.ShowStatus("The document isn't ready to export yet.");
            return false;
        }

        _printFallbackArmedAt = null;   // the export leaves print mode itself when it is done
        await SetPrintModeAsync(true);
        if (_disposed)
        {
            return false;
        }

        try
        {
            CoreWebView2PrintSettings settings = core.Environment.CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = false;
            bool ok = await core.PrintToPdfAsync(pdfPath, settings);
            if (ok)
            {
                _log.Write(AppLogLevel.Info, Category, $"Exported PDF: {pdfPath}");
                _status.ShowStatus($"Exported to {pdfPath}");
            }
            else
            {
                _log.Write(AppLogLevel.Warning, Category, $"PrintToPdfAsync returned false for {pdfPath}.");
                _status.ShowStatus("Couldn't export the PDF.");
            }

            return ok;
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Warning, Category, $"Exporting {pdfPath} failed.", ex);
            _status.ShowStatus("Couldn't export the PDF.");
            return false;
        }
        finally
        {
            if (!_disposed)
            {
                Post(new PrintModeMessage(false));
            }
        }
    }

    public async Task CapturePreviewAsync(Stream pngDestination)
    {
        _dispatcher.VerifyAccess();
        ArgumentNullException.ThrowIfNull(pngDestination);
        CoreWebView2 core = _channel?.Core ?? throw new InvalidOperationException("The document's WebView isn't ready.");
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, pngDestination);
    }

    /// The view couldn't show the page at all (WPF error). Waiters fail; ItemStatus becomes error:&lt;kind&gt;.
    internal void ReportHostError(DocumentErrorKind kind)
    {
        _dispatcher.VerifyAccess();
        if (_disposed)
        {
            return;
        }

        _hostErrorKind = kind;
        State = DocumentSessionState.Error;
        ErrorKind = kind;
        FailRenderWaiters();
        RaiseStateChanged();
    }

    /// The WPF error's "Try again": the page is reloading, the payload is re-posted on 'ready'.
    internal void ClearHostError()
    {
        _dispatcher.VerifyAccess();
        if (_disposed || _hostErrorKind is null)
        {
            return;
        }

        _hostErrorKind = null;
        if (_payload is { IsError: true, ErrorKind: { } kind })
        {
            ErrorKind = kind;   // the page will show the document error again
        }
        else
        {
            State = DocumentSessionState.Loading;
            ErrorKind = null;
            _contentVersion = 0;
            RenderedVersion = 0;   // the reloaded page renders the payload again
        }

        RaiseStateChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pipelineCts.Cancel();
        _theme.EffectiveThemeChanged -= OnEffectiveThemeChanged;
        _settings.Changed -= OnSettingsChanged;

        IDocumentWatcher? watcher;
        lock (_watcherLock)
        {
            watcher = _watcher;
            _watcher = null;
        }

        if (watcher is not null)
        {
            watcher.Changed -= OnWatcherChanged;
            // Closing a FileSystemWatcher's directory handle can block on a hung network share: not on the UI thread.
            _ = Task.Run(() => DisposeQuietly(watcher));
        }

        if (_channel is { } channel)
        {
            channel.Ready -= OnChannelReady;
            channel.MessageReceived -= OnChannelMessage;
            channel.FilesDropped -= OnFilesDropped;
        }

        _printModeWaiter?.Completion.TrySetResult();
        _printModeWaiter = null;
        foreach ((_, TaskCompletionSource completion) in _renderWaiters)
        {
            completion.TrySetCanceled();
        }

        _renderWaiters.Clear();
        _log.Write(AppLogLevel.Debug, Category, $"Closed {Path}.");
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Pipeline (§5 versioning rules 1–5)

    private async Task ReloadCoreAsync(bool preserveScroll, string? scrollToId, bool force)
    {
        if (_disposed)
        {
            return;
        }

        int version = ++_version;
        _pipelineCts.Cancel();
        var cts = new CancellationTokenSource();
        _pipelineCts = cts;
        bool entered = false;
        try
        {
            await _pipelineGate.WaitAsync(cts.Token);
            entered = true;
            await ReturnToUiThread();
            if (version != _version || _disposed)
            {
                return;   // superseded while waiting for the previous pipeline
            }

            bool hasContent = _payload is { IsError: false };
            var request = new PipelineRequest(
                version,
                preserveScroll && hasContent,
                scrollToId ?? (hasContent ? null : _pendingFragment),
                force ? null : _lastLoaded,
                LocalResourcesUnavailable ? LocalResourcesBanner : null);

            PipelineOutcome outcome = await Task.Run(() => RunPipelineAsync(request, cts.Token), cts.Token);
            await ReturnToUiThread();
            if (version != _version || _disposed)
            {
                return;   // rule 3: a newer reload started meanwhile
            }

            ApplyOutcome(outcome);
        }
        catch (OperationCanceledException)
        {
            // Superseded or disposed.
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, Category, $"Reloading {Path} failed unexpectedly.", ex);
        }
        finally
        {
            if (entered)
            {
                _pipelineGate.Release();
            }
        }
    }

    /// Thread pool: load → decode → render (parse + sanitize) → serialize. Never touches UI state.
    private async Task<PipelineOutcome> RunPipelineAsync(PipelineRequest request, CancellationToken cancellationToken)
    {
        string resourceRoot = await ResourceRootTask.ConfigureAwait(false);
        long started = Stopwatch.GetTimestamp();
        DocumentLoadResult result;
        try
        {
            result = await _loader.LoadAsync(Path, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, Category, $"The loader threw for {Path}.", ex);
            result = new DocumentLoadFailed(Path, DocumentLoadError.IoError, ex.Message);
        }

        double loadMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        cancellationToken.ThrowIfCancellationRequested();

        if (result is DocumentLoadFailed failed)
        {
            return new FailedOutcome(failed.Error, failed.Detail, SerializeError(ToErrorKind(failed.Error), failed.Detail));
        }

        var loaded = (DocumentLoaded)result;
        BannerInfo? encodingBanner = loaded.UsedFallbackEncoding
            ? DocumentErrorMessages.FallbackEncodingBanner(loaded.EncodingName)
            : null;
        var snapshot = new LoadedSnapshot(loaded.Text, loaded.EncodingName);
        if (request.Previous is { } previous
            && string.Equals(previous.EncodingName, snapshot.EncodingName, StringComparison.Ordinal)
            && string.Equals(previous.Text, snapshot.Text, StringComparison.Ordinal))
        {
            return new UnchangedOutcome(encodingBanner);   // rule 4
        }

        try
        {
            RenderResult render = _renderer.Render(loaded.Text, new RenderContext(Path, resourceRoot), cancellationToken);
            BannerInfo? banner = encodingBanner ?? request.ResourceBanner;
            IReadOnlyList<string> parts = ProtocolSerializer.SerializeRender(
                DocId, request.Version, render, request.PreserveScroll, request.ScrollToId, banner);
            var perfInfo = new PerfDocumentInfo(Path, loaded.ByteLength, loadMs, render.Timings.ParseMs, render.Timings.HtmlMs,
                render.Timings.SanitizeMs, render.Html.Length, parts.Count, 0);
            return new RenderedOutcome(request.Version, snapshot, parts, render.Title, request.ScrollToId, encodingBanner, banner, perfInfo);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, Category, $"Rendering {Path} failed.", ex);
            return new RenderCrashedOutcome(ex.Message, SerializeError(DocumentErrorKind.RenderFailed, ex.Message));
        }
    }

    /// UI thread: the outcome is current (not superseded).
    private void ApplyOutcome(PipelineOutcome outcome)
    {
        bool isFirstAttempt = !_hadOutcome;
        _hadOutcome = true;
        bool hasContent = _payload is { IsError: false };

        switch (outcome)
        {
            case RenderedOutcome rendered:
                _lastLoaded = rendered.Snapshot;
                _deleted = false;
                _reloadBanner = null;
                _encodingBanner = rendered.EncodingBanner;
                _perfInfo = rendered.PerfInfo;
                _payload = new Payload(rendered.Parts, rendered.Version, IsError: false, null, rendered.PayloadBanner);
                _payloadPosted = false;
                if (_pendingFragment is not null && _pendingFragment == rendered.ScrollToId)
                {
                    _pendingFragment = null;   // baked into this payload's scrollToId
                }

                Title = rendered.Title;
                if (State == DocumentSessionState.Error && _hostErrorKind is null)
                {
                    State = DocumentSessionState.Loading;
                    ErrorKind = null;
                }

                RaiseStateChanged();
                RaiseLoadCompleted(null, isFirstAttempt);
                _ = PostCurrentPayloadAsync();
                break;

            case UnchangedOutcome unchanged:
                _deleted = false;
                _reloadBanner = null;
                _encodingBanner = unchanged.EncodingBanner;
                RaiseStateChanged();
                RaiseLoadCompleted(null, isFirstAttempt);
                PostBannerIfChanged();
                break;

            case FailedOutcome failed:
                LogLoadFailure(failed, hasContent);
                if (hasContent && KeepsContent(failed.Error))
                {
                    if (failed.Error == DocumentLoadError.NotFound)
                    {
                        _deleted = true;
                    }
                    else
                    {
                        _reloadBanner = DocumentErrorMessages.ReloadFailedBanner(ToErrorKind(failed.Error), failed.Detail);
                    }

                    RaiseStateChanged();
                    PostBannerIfChanged();
                }
                else
                {
                    ShowErrorPayload(ToErrorKind(failed.Error), failed.ErrorJson);
                }

                RaiseLoadCompleted(failed.Error, isFirstAttempt);
                break;

            case RenderCrashedOutcome crashed:
                if (hasContent)
                {
                    _reloadBanner = DocumentErrorMessages.ReloadFailedBanner(DocumentErrorKind.RenderFailed, crashed.Detail);
                    RaiseStateChanged();
                    PostBannerIfChanged();
                }
                else
                {
                    ShowErrorPayload(DocumentErrorKind.RenderFailed, crashed.ErrorJson);
                }

                RaiseLoadCompleted(null, isFirstAttempt);   // the file itself was read fine
                break;
        }
    }

    private void ShowErrorPayload(DocumentErrorKind kind, string errorJson)
    {
        _payload = new Payload([errorJson], 0, IsError: true, kind, null);
        _payloadPosted = false;
        _lastLoaded = null;
        _reloadBanner = null;
        _perfInfo = null;
        Title = "";
        State = DocumentSessionState.Error;
        ErrorKind = _hostErrorKind ?? kind;
        FailRenderWaiters();
        RaiseStateChanged();
        _ = PostCurrentPayloadAsync();
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Posting (§7.1 rules 1–3)

    private async Task PostCurrentPayloadAsync()
    {
        IWebViewChannel? channel = _channel;
        Payload? payload = _payload;
        if (_disposed || channel is null || !channel.IsReady || payload is null)
        {
            return;
        }

        int generation = ++_postGeneration;
        _payloadPosted = false;
        _postedCompleteVersion = 0;
        try
        {
            if (!payload.IsError && channel is WebViewBridge bridge)
            {
                bridge.EnsureDocumentRootMapped();
            }

            for (int i = 0; i < payload.Parts.Count; i++)
            {
                if (i > 0)
                {
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    if (generation != _postGeneration || _disposed || !channel.IsReady)
                    {
                        return;   // superseded by a newer payload or a page reload; never interleave
                    }
                }

                channel.Post(payload.Parts[i]);
            }

            _payloadPosted = true;
            if (payload.IsError)
            {
                return;
            }

            _postedCompleteVersion = payload.Version;   // only now may the page report 'rendered' for it
            _postedBanner = payload.Banner;
            _perf.Mark(PerfMarks.FirstRenderPosted);
            if (_pendingFragment is { } fragment)
            {
                _pendingFragment = null;
                Post(new ScrollToMessage(fragment));
            }

            PostBannerIfChanged();
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, Category, $"Posting the payload of {Path} failed.", ex);
        }
    }

    private void PostBannerIfChanged()
    {
        if (_payload is not { IsError: false } || !_payloadPosted)
        {
            return;   // the banner travels with the next render payload
        }

        BannerInfo? banner = EffectiveBanner();
        if (banner == _postedBanner)
        {
            return;
        }

        if (Post(new BannerMessage(banner)))
        {
            _postedBanner = banner;
        }
    }

    private BannerInfo? EffectiveBanner()
    {
        if (_deleted)
        {
            return DeletedBanner;
        }

        return _reloadBanner ?? _encodingBanner ?? (LocalResourcesUnavailable ? LocalResourcesBanner : null);
    }

    private bool LocalResourcesUnavailable => _channel is WebViewBridge { LocalResourcesUnavailable: true };

    /// What we know about the page is gone (new page load, or a replaced WebView).
    private void ResetPageState()
    {
        _payloadPosted = false;
        _postedCompleteVersion = 0;
        _postedBanner = null;
        _postedTocVisible = null;
        _postedReadingStyle = null;
        _printFallbackArmedAt = null;
    }

    private bool Post(HostMessage message)
    {
        IWebViewChannel? channel = _channel;
        if (_disposed || channel is null || !channel.IsReady)
        {
            return false;
        }

        channel.Post(ProtocolSerializer.Serialize(message));
        return true;
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Channel events

    private void OnChannelReady(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            // §7.1 rule 2 + §14: theme, readingStyle, tocVisibility, then the current payload. A fresh page is not in
            // print mode.
            ResetPageState();
            AppSettings settings = _settings.Current;
            bool tocVisible = settings.TocVisible;
            Post(new ThemeMessage(_theme.EffectiveTheme));
            if (Post(new ReadingStyleMessage(settings.ReadingStyle)))
            {
                _postedReadingStyle = settings.ReadingStyle;
            }

            if (Post(new TocVisibilityMessage(tocVisible)))
            {
                _postedTocVisible = tocVisible;
            }

            _ = PostCurrentPayloadAsync();
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, Category, "Posting the initial page state failed.", ex);
        }
    }

    private void OnChannelMessage(object? sender, WebMessage message)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            switch (message)
            {
                case LinkMessage link:
                    _ = HandleLinkAsync(link);
                    break;
                case CopyMessage copy:
                    if (!_clipboard.TrySetText(copy.Text))
                    {
                        _status.ShowStatus("Couldn't copy");
                    }

                    break;
                case RetryMessage:
                    _ = ReloadCoreAsync(preserveScroll: false, scrollToId: null, force: true);
                    break;
                case RenderedMessage rendered:
                    HandleRendered(rendered);
                    break;
                case TocVisibilityChangedMessage toc:
                    _postedTocVisible = toc.Visible;   // the page already shows it
                    _settings.Update(s => s.TocVisible == toc.Visible ? s : s with { TocVisible = toc.Visible });
                    break;
                case LogMessage log:
                    HandleWebLog(log);
                    break;
                case PrintModeReadyMessage printMode:
                    if (_printModeWaiter is { } waiter && waiter.Enabled == printMode.Enabled)
                    {
                        waiter.Completion.TrySetResult();
                    }

                    break;
                default:
                    _log.Write(AppLogLevel.Debug, Category, $"Ignored web message {message.GetType().Name}.");
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, Category, $"Handling {message.GetType().Name} failed.", ex);
        }
    }

    private async Task HandleLinkAsync(LinkMessage link)
    {
        try
        {
            string resourceRoot = await ResourceRootTask;
            string documentPath = Path;
            // The classifier may probe the file system (directory README): keep that off the UI thread.
            LinkTarget target = await Task.Run(() => _linkClassifier.Classify(link.Href, documentPath, resourceRoot));
            await ReturnToUiThread();
            if (_disposed)
            {
                return;
            }

            switch (target)
            {
                case LinkTarget.Anchor anchor:
                    ScrollTo(anchor.Fragment);
                    break;
                case LinkTarget.MarkdownDocument document:
                    _opener.Open(document.FullPath, document.Fragment, activate: !link.NewTab);
                    break;
                case LinkTarget.External external:
                    if (!_launcher.TryOpen(external.Uri))
                    {
                        _status.ShowStatus("Couldn't open the link.");
                    }

                    break;
                case LinkTarget.Blocked blocked:
                    _log.Write(AppLogLevel.Info, Category, $"Blocked link '{WebViewSecurity.Shorten(link.Href)}': {blocked.Reason}");
                    _status.ShowStatus(blocked.Reason);
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, Category, $"Handling the link '{WebViewSecurity.Shorten(link.Href)}' failed.", ex);
        }
    }

    private void HandleRendered(RenderedMessage rendered)
    {
        if (rendered.DocId != DocId)
        {
            _log.Write(AppLogLevel.Debug, Category, $"Ignored 'rendered' for doc {rendered.DocId} (this is {DocId}).");
            return;
        }

        if (_payload is not { IsError: false } payload || rendered.Version != payload.Version)
        {
            return;   // stale: a newer payload is on its way
        }

        if (rendered.Version != _postedCompleteVersion)
        {
            // Not every part of this version has reached the current page yet (or it went to a page that has since
            // been replaced), so the page can't have rendered it: a stray message, ignore it.
            _log.Write(AppLogLevel.Debug, Category, $"Ignored 'rendered' v{rendered.Version} before all its parts were posted.");
            return;
        }

        if (rendered.Phase == RenderPhase.Content)
        {
            _contentVersion = rendered.Version;
            if (State == DocumentSessionState.Loading)
            {
                State = DocumentSessionState.Rendered;
            }

            _perf.Mark(PerfMarks.FirstRenderedContent);
            if (_perf.IsEnabled && _perfInfo is { } info)
            {
                _perf.SetDocument(info with { WebContentMs = rendered.Ms });
            }
        }
        else
        {
            _contentVersion = Math.Max(_contentVersion, rendered.Version);
            RenderedVersion = rendered.Version;
            if (State == DocumentSessionState.Loading)
            {
                State = DocumentSessionState.Rendered;
            }

            _perf.Mark(PerfMarks.FirstRenderedEnhanced);
        }

        RaiseStateChanged();
        CompleteRenderWaiters();
    }

    private void HandleWebLog(LogMessage message)
    {
        long now = _time.GetTimestamp();
        if (_logWindowCount == 0 || _time.GetElapsedTime(_logWindowStart, now) >= TimeSpan.FromSeconds(1))
        {
            if (_logSuppressed > 0)
            {
                _log.Write(AppLogLevel.Warning, WebCategory, $"{_logSuppressed} web log message(s) suppressed (limit {MaxWebLogMessagesPerSecond}/s).");
            }

            _logWindowStart = now;
            _logWindowCount = 0;
            _logSuppressed = 0;
        }

        if (++_logWindowCount > MaxWebLogMessagesPerSecond)
        {
            _logSuppressed++;
            return;
        }

        AppLogLevel level = message.Level switch
        {
            WebLogLevel.Debug => AppLogLevel.Debug,
            WebLogLevel.Info => AppLogLevel.Info,
            WebLogLevel.Warn => AppLogLevel.Warning,
            _ => AppLogLevel.Error,
        };
        // Document-derived text (e.g. a Mermaid parse error quoting the source): escape CR/LF and other control
        // characters and cap it, so it stays one log line and can't forge entries.
        _log.Write(level, WebCategory, $"[doc {DocId}] {WebViewSecurity.ForLog(message.Message, MaxLoggedMessageChars)}");
    }

    private void OnFilesDropped(object? sender, IReadOnlyList<string> files)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var markdown = new List<string>(files.Count);
            foreach (string file in files)
            {
                if (!string.IsNullOrWhiteSpace(file) && MarkdownFileTypes.IsMarkdownPath(file))
                {
                    markdown.Add(file);
                }
            }

            for (int i = 0; i < markdown.Count; i++)
            {
                _opener.Open(markdown[i], null, activate: i == 0);
            }

            if (markdown.Count < files.Count)
            {
                _log.Write(AppLogLevel.Info, Category, $"Skipped {files.Count - markdown.Count} dropped non-Markdown file(s).");
                _status.ShowStatus(markdown.Count == 0
                    ? "Only Markdown files open in MdReader."
                    : "Some files were skipped: only Markdown files open in MdReader.");
            }
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, Category, "Opening dropped files failed.", ex);
        }
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Theme, settings, watcher

    private void OnEffectiveThemeChanged(object? sender, EventArgs e)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnEffectiveThemeChanged(sender, e));
            return;
        }

        if (!_disposed)
        {
            Post(new ThemeMessage(_theme.EffectiveTheme));
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnSettingsChanged(sender, settings));
            return;
        }

        if (_disposed)
        {
            return;
        }

        // §14: every tab follows the reading style as soon as it changes (no re-render: the page swaps data-style).
        if (_postedReadingStyle != settings.ReadingStyle && Post(new ReadingStyleMessage(settings.ReadingStyle)))
        {
            _postedReadingStyle = settings.ReadingStyle;
        }

        if (_postedTocVisible != settings.TocVisible && Post(new TocVisibilityMessage(settings.TocVisible)))
        {
            _postedTocVisible = settings.TocVisible;
        }
    }

    private void StartWatcher()
    {
        // Creating a FileSystemWatcher touches the file system (and possibly the network): not on the UI thread.
        _ = Task.Run(() =>
        {
            IDocumentWatcher watcher;
            try
            {
                watcher = _watcherFactory.Create(Path);
            }
            catch (Exception ex)
            {
                _log.Write(AppLogLevel.Warning, Category, $"Couldn't watch {Path} for changes; F5 still reloads.", ex);
                return;
            }

            lock (_watcherLock)
            {
                if (!_disposed)
                {
                    watcher.Changed += OnWatcherChanged;
                    _watcher = watcher;
                    return;
                }
            }

            DisposeQuietly(watcher);
        });
    }

    /// Thread pool (debounced). Marshal to the UI thread (§5).
    private void OnWatcherChanged(object? sender, DocumentChangedEventArgs e)
    {
        DocumentChangeKind kind = e.Kind;
        _dispatcher.InvokeAsync(() => HandleWatcherChange(kind));
    }

    private void HandleWatcherChange(DocumentChangeKind kind)
    {
        if (_disposed)
        {
            return;
        }

        if (kind == DocumentChangeKind.Deleted)
        {
            _log.Write(AppLogLevel.Info, Category, $"File deleted or moved: {Path}");
            if (!_deleted)
            {
                _deleted = true;
                RaiseStateChanged();
                PostBannerIfChanged();
            }

            return;
        }

        _ = ReloadCoreAsync(preserveScroll: true, scrollToId: null, force: false);
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Helpers

    private string ResolveResourceRoot()
    {
        string fallback = IOPath.GetDirectoryName(Path) ?? Path;
        try
        {
            string root = _rootResolver.Resolve(Path);
            _log.Write(AppLogLevel.Info, Category, $"Resource root for {Path}: {root}");
            return root;
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Warning, Category, $"Resolving the resource root of {Path} failed; using its folder.", ex);
            return fallback;
        }
    }

    private CoreWebView2? ReadyCore() =>
        !_disposed && _channel is { IsReady: true } channel ? channel.Core : null;

    private async Task SetPrintModeAsync(bool enabled)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _printModeWaiter?.Completion.TrySetResult();
        _printModeWaiter = (enabled, completion);
        try
        {
            if (!Post(new PrintModeMessage(enabled)))
            {
                return;
            }

            await completion.Task.WaitAsync(PrintModeTimeout, _time);
        }
        catch (TimeoutException)
        {
            _log.Write(AppLogLevel.Warning, Category, $"The page didn't confirm printMode={enabled} within {PrintModeTimeout.TotalSeconds:0} s; continuing.");
        }
        finally
        {
            await ReturnToUiThread();
            if (_printModeWaiter is { } current && current.Completion == completion)
            {
                _printModeWaiter = null;
            }
        }
    }

    private bool IsRendered(RenderPhase phase)
    {
        if (_payload is not { IsError: false } payload || _hostErrorKind is not null)
        {
            return false;
        }

        int reached = phase == RenderPhase.Content ? _contentVersion : RenderedVersion;
        return reached >= payload.Version;
    }

    private async Task WaitForRenderAsync(TaskCompletionSource completion, TimeSpan timeout)
    {
        try
        {
            await completion.Task.WaitAsync(timeout, _time);
        }
        finally
        {
            await ReturnToUiThread();
            _renderWaiters.RemoveAll(w => w.Completion == completion);
        }
    }

    /// Continues on the session's UI thread. Sessions are created by DocumentOpener, which also runs before
    /// Application.Run (CLI files, --capture) when no SynchronizationContext exists yet, so awaits must not rely on the
    /// ambient context to get back to the UI thread.
    private DispatcherAwaitable ReturnToUiThread() => new(_dispatcher);

    private readonly struct DispatcherAwaitable(Dispatcher dispatcher) : INotifyCompletion
    {
        public DispatcherAwaitable GetAwaiter() => this;

        public bool IsCompleted => dispatcher.CheckAccess();

        public void OnCompleted(Action continuation) => dispatcher.BeginInvoke(DispatcherPriority.Normal, continuation);

        public void GetResult()
        {
        }
    }

    private void CompleteRenderWaiters()
    {
        foreach ((RenderPhase phase, TaskCompletionSource completion) in _renderWaiters.ToArray())
        {
            if (IsRendered(phase))
            {
                completion.TrySetResult();
            }
        }
    }

    private void FailRenderWaiters()
    {
        foreach ((_, TaskCompletionSource completion) in _renderWaiters.ToArray())
        {
            completion.TrySetException(CreateRenderFailedException());
        }
    }

    private InvalidOperationException CreateRenderFailedException() =>
        new($"The document couldn't be displayed ({ErrorKind?.ToString() ?? "error"}): {Path}");

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, Category, "A StateChanged handler failed.", ex);
        }
    }

    private void RaiseLoadCompleted(DocumentLoadError? error, bool isFirstAttempt)
    {
        try
        {
            LoadCompleted?.Invoke(this, new DocumentLoadCompletedEventArgs(error, isFirstAttempt));
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, Category, "A LoadCompleted handler failed.", ex);
        }
    }

    private void LogLoadFailure(FailedOutcome failed, bool hasContent)
    {
        AppLogLevel level = failed.Error is DocumentLoadError.AccessDenied or DocumentLoadError.IoError
            ? AppLogLevel.Warning
            : AppLogLevel.Info;
        string phase = hasContent ? "Reloading" : "Loading";
        _log.Write(level, Category, $"{phase} {Path} failed: {failed.Error} ({WebViewSecurity.ForLog(failed.Detail, 500)})");
    }

    private string SerializeError(DocumentErrorKind kind, string? detail)
    {
        string title;
        string message;
        try
        {
            (title, message) = DocumentErrorMessages.For(kind, Path, detail);
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, Category, "DocumentErrorMessages.For failed.", ex);
            (title, message) = ("Couldn't display this document", detail ?? "");
        }

        return ProtocolSerializer.Serialize(new ErrorMessage(kind, title, message, Path));
    }

    private static bool KeepsContent(DocumentLoadError error) =>
        error is DocumentLoadError.NotFound or DocumentLoadError.AccessDenied or DocumentLoadError.Locked or DocumentLoadError.IoError;

    internal static DocumentErrorKind ToErrorKind(DocumentLoadError error) => error switch
    {
        DocumentLoadError.NotFound => DocumentErrorKind.NotFound,
        DocumentLoadError.AccessDenied => DocumentErrorKind.AccessDenied,
        DocumentLoadError.Locked => DocumentErrorKind.Locked,
        DocumentLoadError.Binary => DocumentErrorKind.Binary,
        DocumentLoadError.TooLarge => DocumentErrorKind.TooLarge,
        _ => DocumentErrorKind.IoError,
    };

    /// Protocol spelling of an error kind ("notFound", "renderFailed", …), used for automation ItemStatus.
    internal static string ToProtocolName(DocumentErrorKind kind) => JsonNamingPolicy.CamelCase.ConvertName(kind.ToString());

    private void DisposeQuietly(IDisposable disposable)
    {
        try
        {
            disposable.Dispose();
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Debug, Category, "Disposing the watcher failed.", ex);
        }
    }

    private sealed record LoadedSnapshot(string Text, string EncodingName);

    private sealed record PipelineRequest(int Version, bool PreserveScroll, string? ScrollToId, LoadedSnapshot? Previous, BannerInfo? ResourceBanner);

    private sealed record Payload(IReadOnlyList<string> Parts, int Version, bool IsError, DocumentErrorKind? ErrorKind, BannerInfo? Banner);

    private abstract record PipelineOutcome;

    private sealed record RenderedOutcome(int Version, LoadedSnapshot Snapshot, IReadOnlyList<string> Parts, string Title,
        string? ScrollToId, BannerInfo? EncodingBanner, BannerInfo? PayloadBanner, PerfDocumentInfo PerfInfo) : PipelineOutcome;

    private sealed record UnchangedOutcome(BannerInfo? EncodingBanner) : PipelineOutcome;

    private sealed record FailedOutcome(DocumentLoadError Error, string Detail, string ErrorJson) : PipelineOutcome;

    private sealed record RenderCrashedOutcome(string Detail, string ErrorJson) : PipelineOutcome;
}
