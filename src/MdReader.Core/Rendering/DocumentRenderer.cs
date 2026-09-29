using MdReader.Core.Json;
using MdReader.Core.Paths;
using MdReader.Core.Rendering.Sanitization;

namespace MdReader.Core.Rendering;

/// <summary>
/// The renderer the shells hold: it decides what kind of document it has been given and renders it that way.
/// </summary>
/// <remarks>
/// <para>A <c>.json</c> file goes to <see cref="JsonRenderer"/>, everything else to <see cref="MarkdownRenderer"/>.
/// Putting the decision here rather than at the call sites means nothing downstream changes — the shells, the
/// document session, the export and the web version all keep asking one interface for HTML, and a document that
/// happens to be JSON simply comes back looking like JSON.</para>
/// <para><b>The JSON output is sanitized too</b>, and that is a deliberate choice rather than an oversight.
/// <see cref="JsonRenderer"/> writes every element itself and puts every piece of the document's own text through
/// one escaping function, so there is nothing in its output for an allow-list to remove — the suite proves it, by
/// asserting that sanitizing the output returns it character for character over a corpus built to break it. That
/// proof is what makes sanitizing here free: it can change nothing today, so it costs only time, and it means the
/// sentence in ARCHITECTURE §4.1 — that HTML leaves Core through one door, with the sanitizer behind it — stays
/// true of every document this application opens. If a bug ever did put a stray angle bracket in that output, the
/// fixed-point test fails in the suite and this layer stops it reaching a reader in the meantime. Keeping both is
/// the point; either alone is weaker.</para>
/// </remarks>
public sealed class DocumentRenderer : IMarkdownRenderer
{
    private readonly IMarkdownRenderer _markdown;
    private readonly HtmlContentSanitizer _sanitizer;
    private readonly JsonRenderOptions? _jsonOptions;

    public DocumentRenderer(IFileSystemProbe fileSystem, JsonRenderOptions? jsonOptions = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _markdown = new MarkdownRenderer(fileSystem);
        _sanitizer = new HtmlContentSanitizer(new ResourceUrlRewriter(fileSystem));
        _jsonOptions = jsonOptions;
    }

    public RenderResult Render(string markdown, RenderContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(context);

        if (!JsonFileTypes.IsJsonPath(context.DocumentPath))
        {
            return _markdown.Render(markdown, context, cancellationToken);
        }

        var rendered = JsonRenderer.Render(markdown, _jsonOptions, cancellationToken)
            .ToRenderResult(TitleFor(context));

        var sanitized = _sanitizer.Sanitize(rendered.Html, context, cancellationToken);
        return rendered with { Html = sanitized.Html };
    }

    /// <summary>
    /// The document's file name, which is what the Markdown path falls back to when a document has no first heading.
    /// A JSON document never has one, so this is always the title.
    /// </summary>
    private static string TitleFor(RenderContext context)
    {
        var path = context.DocumentPath;
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        // A remote document's "path" is its URL, whose last segment is the file name; Path.GetFileName on a URL
        // gives the same answer here because the separators it looks for include '/'.
        var name = Path.GetFileName(path);
        return name.Length > 0 ? name : path;
    }
}
