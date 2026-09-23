using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using Ganss.Xss;

namespace MdReader.Core.Rendering.Sanitization;

/// <summary>
/// Allow-list sanitizer (ARCHITECTURE §4.2, §8.2): HtmlSanitizer does parsing, tag/attribute removal and URL filtering;
/// <see cref="SanitizedHtmlWriter"/> applies the per-element rules and serializes.
/// </summary>
/// <remarks>
/// <para>A new <see cref="HtmlSanitizer"/> is created per call because the event handlers capture the per-document
/// <see cref="RenderContext"/>. Only the <c>FilterUrl</c> and <c>RemovingTag</c> hooks are used; <c>PostProcessNode</c> /
/// <c>PostProcessDom</c> are deliberately not subscribed (≈1 s extra on 8 MB); post-processing happens in the writer.</para>
/// <para>Unwrapping a disallowed element can leave a nesting the HTML parser never produces, e.g.
/// <c>&lt;li&gt;&lt;center&gt;&lt;li&gt;</c> → <c>li &gt; li</c> or <c>&lt;h1&gt;&lt;font&gt;&lt;h2&gt;</c> →
/// <c>h1 &gt; h2</c>. The browser would re-nest such output when it parses it, i.e. build a different tree than the one
/// that was sanitized. Only allowed, already sanitized tokens are involved (no raw-text or foreign-content element is
/// ever emitted, so the tokenizer sees exactly our tokens), but to keep "sanitized DOM == browser DOM" and idempotence
/// exact, the output of such a document is sanitized once more: the second pass unwraps nothing, so it is a fixed
/// point. Documents without such unwraps (the normal case) are processed once.</para>
/// <para>Resource limits (<see cref="RenderLimits"/>): each pass pre-scans the nesting depth
/// (<see cref="HtmlNestingScanner"/>), parses under an element budget (<see cref="ElementBudget"/>) and serializes
/// under an output budget. Exceeding one throws <see cref="RenderLimitExceededException"/>; the renderer then shows the
/// document as plain text.</para>
/// </remarks>
internal sealed class HtmlContentSanitizer : IHtmlContentSanitizer
{
    private readonly ResourceUrlRewriter _rewriter;

    public HtmlContentSanitizer(ResourceUrlRewriter rewriter)
    {
        ArgumentNullException.ThrowIfNull(rewriter);
        _rewriter = rewriter;
    }

    public SanitizedHtml Sanitize(string html, RenderContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(html);
        return Sanitize(html, context, html.Length, null, cancellationToken);
    }

    /// <summary>
    /// As <see cref="Sanitize(string, RenderContext, CancellationToken)"/>, with the element and output budgets based on
    /// <paramref name="sourceLength"/> (the length of the Markdown the HTML was rendered from) as well as on the HTML, so
    /// HTML that Markdig amplified doesn't earn a proportionally bigger budget, and with the render's task-line token
    /// (<paramref name="taskLinePrefix"/>, §4.1).
    /// </summary>
    internal SanitizedHtml Sanitize(string html, RenderContext context, int sourceLength, string? taskLinePrefix,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        var result = SanitizeOnce(html, context, sourceLength, taskLinePrefix, cancellationToken, out var restructured);
        if (restructured)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = SanitizeOnce(result.Html, context, sourceLength, taskLinePrefix, cancellationToken, out _);
        }

        return result;
    }

    /// <summary>
    /// The HtmlSanitizer stage alone: parse, remove disallowed tags/attributes, filter URLs. The caller owns (disposes)
    /// the document. Internal so tests can compare <see cref="SanitizedHtmlWriter"/> with AngleSharp's serializer.
    /// </summary>
    internal IHtmlDocument SanitizeDom(string html, RenderContext context) =>
        SanitizeDom(html, html.Length, new Session(_rewriter, context));

    /// <summary>
    /// The sanitized <c>body</c>, or null. A leading <c>&lt;frameset&gt;</c> can replace the body; <c>Body</c> is then the
    /// frameset (whose children are all disallowed) and nothing is written.
    /// </summary>
    internal static IElement? GetBody(IHtmlDocument document)
    {
        var body = document.Body;
        return body is not null && string.Equals(body.LocalName, "body", StringComparison.Ordinal) ? body : null;
    }

    /// <exception cref="RenderLimitExceededException">The DOM would exceed <see cref="RenderLimits.ElementBudget"/>.</exception>
    private static IHtmlDocument SanitizeDom(string html, int sourceLength, Session session)
    {
        var sanitizer = new HtmlSanitizer(SanitizerPolicy.Options)
        {
            KeepChildNodes = true,
            HtmlParserFactory = ElementBudget.CreateParser,
        };
        sanitizer.FilterUrl += session.OnFilterUrl;
        sanitizer.RemovingTag += session.OnRemovingTag;
        using (ElementBudget.Enter(RenderLimits.ElementBudget(html.Length, sourceLength)))
        {
            return sanitizer.SanitizeDom(html);
        }
    }

    /// <exception cref="RenderLimitExceededException">The input nests too deeply, or the DOM or the output would exceed
    /// their budgets (<see cref="RenderLimits"/>).</exception>
    private SanitizedHtml SanitizeOnce(
        string html, RenderContext context, int sourceLength, string? taskLinePrefix, CancellationToken cancellationToken,
        out bool restructured)
    {
        restructured = false;
        if (html.Length == 0)
        {
            return new SanitizedHtml(string.Empty, []);
        }

        // Linear pre-scan, so pathological nesting never reaches AngleSharp's quadratic tree construction.
        if (HtmlNestingScanner.Exceeds(html, RenderLimits.MaxHtmlNesting))
        {
            throw new RenderLimitExceededException($"The HTML nests deeper than {RenderLimits.MaxHtmlNesting} levels.");
        }

        var session = new Session(_rewriter, context);
        using var document = SanitizeDom(html, sourceLength, session);
        cancellationToken.ThrowIfCancellationRequested();
        restructured = session.Restructured;

        var body = GetBody(document);
        return body is null
            ? new SanitizedHtml(string.Empty, [])
            : SanitizedHtmlWriter.Write(body, RenderLimits.OutputBudget(html.Length, sourceLength), taskLinePrefix, cancellationToken);
    }

    /// <summary>Per-call hook state: captures the document context, memoizes image URLs (each file is probed once).</summary>
    private sealed class Session(ResourceUrlRewriter rewriter, RenderContext context)
    {
        private readonly Dictionary<string, string?> _imageUrls = new(StringComparer.Ordinal);

        /// <summary>True once an attached disallowed element with element children was unwrapped.</summary>
        public bool Restructured { get; private set; }

        public void OnFilterUrl(object? sender, FilterUrlEventArgs e)
        {
            // FilterUrl fires for every href/src and each srcset candidate, including URLs HtmlSanitizer's scheme check
            // already rejected (SanitizedUrl == null, OriginalUrl intact). Our rules always start from OriginalUrl.
            e.SanitizedUrl = e.Tag.LocalName switch
            {
                "a" => ResourceUrlRewriter.FilterHref(e.OriginalUrl),
                "img" or "source" => RewriteImageUrl(e.OriginalUrl),
                _ => null,          // no other element may carry a URL attribute
            };
        }

        /// <summary>
        /// With <c>KeepChildNodes = true</c> a disallowed tag is unwrapped; for drop-subtree tags the children are removed
        /// first, so no script, CSS or fallback text leaks out.
        /// </summary>
        public void OnRemovingTag(object? sender, RemovingTagEventArgs e)
        {
            var tag = e.Tag;
            if (SanitizerPolicy.DropSubtreeTags.Contains(tag.LocalName))
            {
                // Remove from the end: AngleSharp's child list is an array list, so this is O(1) per child.
                while (tag.LastChild is { } child)
                {
                    tag.RemoveChild(child);
                }

                return;
            }

            // Unwrap. Only element children can end up in a nesting the parser would not produce. (Detached tags, i.e.
            // inside an already dropped subtree, are not unwrapped into the document at all.)
            if (!Restructured && tag.Parent is not null && tag.FirstElementChild is not null)
            {
                Restructured = true;
            }
        }

        private string? RewriteImageUrl(string url)
        {
            if (!_imageUrls.TryGetValue(url, out var rewritten))
            {
                rewritten = rewriter.RewriteImageUrl(url, context);
                _imageUrls[url] = rewritten;
            }

            return rewritten;
        }
    }
}
