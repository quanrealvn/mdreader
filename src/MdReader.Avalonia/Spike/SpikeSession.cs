using MdReader.Avalonia.WebView;
using MdReader.Core.Diagnostics;
using MdReader.Core.Documents;
using MdReader.Core.Paths;
using MdReader.Core.Protocol;
using MdReader.Core.Rendering;
using MdReader.Core.Theming;

namespace MdReader.Avalonia.Spike;

/// <summary>
/// The phase-1 stand-in for <c>DocumentSession</c>: load and render one document off the UI thread, post it through the
/// channel when the page reports 'ready', and handle the handful of web messages the spike cares about
/// (<c>ready</c>, <c>link</c>, <c>log</c>, <c>rendered</c>).
/// </summary>
/// <remarks>
/// Nothing here touches Avalonia or WPF; the type only needs a channel and a theme getter. That is the measurement the
/// spike was for: this is the class that moves into the shared shell layer unchanged.
/// UI thread only, apart from the render itself.
/// </remarks>
internal sealed class SpikeSession
{
    private const string Category = "SpikeSession";
    private const int DocId = 1;
    private const int Version = 1;

    private readonly string _path;
    private readonly Func<AppTheme> _theme;
    private readonly IAppLog _log;
    private readonly LinkClassifier _classifier = new(PhysicalFileSystemProbe.Instance);
    private readonly TaskCompletionSource _enhanced = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private WebViewChannel? _channel;
    private IReadOnlyList<string>? _payload;
    private string? _errorPayload;
    private bool _posted;
    private bool _prelude;

    public SpikeSession(string path, Func<AppTheme> theme, IAppLog log)
    {
        _path = path;
        _theme = theme;
        _log = log;

        // Start the pipeline before the WebView2 environment is even up: the render is CPU-bound and must never run on
        // the UI thread (§5). ResourceRootTask mirrors DocumentSession.
        ResourceRootTask = Task.Run(() => new ResourceRootResolver(PhysicalFileSystemProbe.Instance).Resolve(_path));
        RenderTask = Task.Run(RenderAsync);
    }

    /// <summary>Completes when the page reports <c>rendered{phase:"enhanced"}</c> (highlighting, math, Mermaid done).</summary>
    public Task Enhanced => _enhanced.Task;

    public Task<string> ResourceRootTask { get; }

    public Task RenderTask { get; }

    /// <summary>The document title reported by the renderer; "" until the render finishes.</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>A link the page asked to open in the user's browser.</summary>
    public event EventHandler<Uri>? ExternalLinkRequested;

    public void Attach(WebViewChannel channel)
    {
        _channel = channel;
        channel.Ready += (_, _) =>
        {
            _prelude = false;
            PostState();
        };
        channel.MessageReceived += OnMessageReceived;
    }

    /// <summary>Re-posts the theme after the OS light/dark setting changes.</summary>
    public void PostTheme()
    {
        if (_channel is { IsReady: true } channel)
        {
            channel.Post(new ThemeMessage(_theme()));
        }
    }

    /// <summary>A navigation or new-window request the page couldn't intercept; routed exactly like a clicked link.</summary>
    public void RouteLink(Uri uri, bool newTab) => HandleLink(new LinkMessage(uri.AbsoluteUri, newTab));

    /// <summary>Posts theme + payload once the page is ready, and again after every reload (§7.1 rule 2).</summary>
    private void PostState()
    {
        WebViewChannel? channel = _channel;
        if (channel is null || !channel.IsReady)
        {
            return;
        }

        if (!_prelude)
        {
            // §7.1 rule 2: theme and tocVisibility first, then the payload — once per page load.
            _prelude = true;
            channel.Post(new ThemeMessage(_theme()));
            channel.Post(new TocVisibilityMessage(true));
        }

        if (_errorPayload is not null)
        {
            channel.Post(_errorPayload);
            _posted = true;
            return;
        }

        if (_payload is null)
        {
            // The render is still running; RenderAsync posts once it lands.
            return;
        }

        foreach (string part in _payload)
        {
            channel.Post(part);
        }

        _posted = true;
        _log.Write(AppLogLevel.Info, Category, $"Posted the render payload ({_payload.Count} part(s)).");
    }

    private async Task RenderAsync()
    {
        string resourceRoot = await ResourceRootTask.ConfigureAwait(false);
        var loader = new DocumentLoader();
        DocumentLoadResult result = await loader.LoadAsync(_path).ConfigureAwait(false);

        if (result is DocumentLoadFailed failed)
        {
            DocumentErrorKind kind = MapError(failed.Error);
            _log.Write(AppLogLevel.Warning, Category, $"Loading {_path} failed: {failed.Error} ({failed.Detail}).");
            (string title, string message) = DocumentErrorMessages.For(kind, _path, failed.Detail);
            string json = ProtocolSerializer.Serialize(new ErrorMessage(kind, title, message, _path));
            await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => OnPayloadReady(null, json));
            return;
        }

        var loaded = (DocumentLoaded)result;
        var renderer = new MarkdownRenderer(PhysicalFileSystemProbe.Instance);
        RenderResult render = renderer.Render(loaded.Text, new RenderContext(_path, resourceRoot));
        IReadOnlyList<string> parts = ProtocolSerializer.SerializeRender(DocId, Version, render, preserveScroll: false,
            scrollToId: null, banner: null);
        _log.Write(AppLogLevel.Info, Category,
            $"Rendered {_path}: {render.Html.Length} chars, {render.Toc.Count} headings, {parts.Count} part(s), "
            + $"features mermaid={render.Features.Mermaid} math={render.Features.Math} code={render.Features.Code}, "
            + $"{render.Timings.TotalMs:0.0} ms.");

        await global::Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            Title = render.Title;
            OnPayloadReady(parts, null);
        });
    }

    private void OnPayloadReady(IReadOnlyList<string>? parts, string? errorJson)
    {
        _payload = parts;
        _errorPayload = errorJson;
        if (!_posted)
        {
            PostState();
        }
    }

    private void OnMessageReceived(object? sender, WebMessage message)
    {
        switch (message)
        {
            case LinkMessage link:
                HandleLink(link);
                break;

            case LogMessage log:
                // Page text is untrusted: escape it before it reaches a log line.
                _log.Write(MapLevel(log.Level), "Web", WebViewSecurity.ForLog(log.Message));
                break;

            case RenderedMessage rendered:
                _log.Write(AppLogLevel.Info, Category, $"Page rendered doc {rendered.DocId} v{rendered.Version} ({rendered.Phase}) in {rendered.Ms:0.0} ms.");
                if (rendered.Phase == RenderPhase.Enhanced)
                {
                    _enhanced.TrySetResult();
                }

                break;

            default:
                _log.Write(AppLogLevel.Debug, Category, $"Ignored a {message.GetType().Name} (out of scope for phase 1).");
                break;
        }
    }

    private void HandleLink(LinkMessage link)
    {
        LinkTarget target = _classifier.Classify(link.Href, _path, ResourceRootTask.IsCompletedSuccessfully ? ResourceRootTask.Result : null);
        switch (target)
        {
            case LinkTarget.External external:
                _log.Write(AppLogLevel.Info, Category, $"Opening {WebViewSecurity.Shorten(external.Uri.AbsoluteUri)} externally.");
                ExternalLinkRequested?.Invoke(this, external.Uri);
                break;

            case LinkTarget.Anchor anchor:
                _channel?.Post(new ScrollToMessage(anchor.Fragment));
                break;

            default:
                // Phase 1 has no tabs and no status toast: a Markdown target or a blocked link is only logged.
                _log.Write(AppLogLevel.Info, Category,
                    $"Ignored link {WebViewSecurity.Shorten(link.Href)} → {target.GetType().Name} (no tabs in the spike).");
                break;
        }
    }

    private static AppLogLevel MapLevel(WebLogLevel level) => level switch
    {
        WebLogLevel.Debug => AppLogLevel.Debug,
        WebLogLevel.Info => AppLogLevel.Info,
        WebLogLevel.Warn => AppLogLevel.Warning,
        _ => AppLogLevel.Error,
    };

    private static DocumentErrorKind MapError(DocumentLoadError error) => error switch
    {
        DocumentLoadError.NotFound => DocumentErrorKind.NotFound,
        DocumentLoadError.AccessDenied => DocumentErrorKind.AccessDenied,
        DocumentLoadError.Locked => DocumentErrorKind.Locked,
        DocumentLoadError.Binary => DocumentErrorKind.Binary,
        DocumentLoadError.TooLarge => DocumentErrorKind.TooLarge,
        _ => DocumentErrorKind.IoError,
    };
}
