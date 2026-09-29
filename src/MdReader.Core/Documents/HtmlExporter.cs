using System.Text;
using AngleSharp.Dom;
using MdReader.Core.Paths;
using MdReader.Core.Protocol;
using MdReader.Core.Rendering;
using MdReader.Core.Rendering.Sanitization;
using MdReader.Core.Theming;
using IOPath = System.IO.Path;

namespace MdReader.Core.Documents;

/// <summary>Ceilings on what an exported page is allowed to carry away with it.</summary>
/// <remarks>
/// The numbers answer "what is still a file you can send someone". Four megabytes is a generous screenshot and a
/// stingy RAW photograph; thirty-two is more pictures than any document anyone reads in this application has. Both
/// are counted on the bytes read from disk — base64 then adds a third on top, so the worst case reaches disk at
/// roughly 43 MB. An image over the per-file ceiling, or a whole document over the total, is dropped rather than
/// shrunk: re-encoding somebody's picture without being asked is the wrong kind of helpful, and a silently
/// half-carried export is worse than one that says what it left behind.
/// </remarks>
public sealed record HtmlExportOptions
{
    public const long DefaultMaxImageBytes = 4L * 1024 * 1024;

    public const long DefaultMaxTotalImageBytes = 32L * 1024 * 1024;

    /// <summary>Longest rendered diagram the page may hand back, in UTF-16 characters of SVG markup.</summary>
    public const int DefaultMaxDiagramChars = 2 * 1024 * 1024;

    public long MaxImageBytes { get; init; } = DefaultMaxImageBytes;

    public long MaxTotalImageBytes { get; init; } = DefaultMaxTotalImageBytes;

    public int MaxDiagramChars { get; init; } = DefaultMaxDiagramChars;
}

/// <summary>Everything one export needs. <paramref name="BodyHtml"/> is the same HTML the reader's page was given.</summary>
/// <param name="DiagramSvg">
/// The rendered diagrams, in document order, one entry per Mermaid block on the page — the answer to the
/// <c>exportContent</c> message (see <c>MdReader.Shell.Documents.IRenderedPageContent</c>). Empty when the page has no
/// answer, which is the state this build ships in: the diagrams then export as their own source text.
/// </param>
public sealed record HtmlExportRequest(string BodyHtml, string Title, RenderContext Context)
{
    public AppTheme Theme { get; init; } = AppTheme.Light;

    /// <summary>The flattened reader stylesheet (<see cref="HtmlExportStylesheet"/>); empty exports an unstyled page.</summary>
    public string Stylesheet { get; init; } = string.Empty;

    public IReadOnlyList<string?> DiagramSvg { get; init; } = [];

    public HtmlExportOptions Options { get; init; } = new();
}

/// <summary>The page, and an account of what did and did not travel with it.</summary>
/// <param name="ImagesInlined">Local images carried inside the file as <c>data:</c> URIs.</param>
/// <param name="ImagesDropped">Local images left behind: missing, unreadable, too big, or of a type we don't inline.</param>
/// <param name="ImagesLeftRemote">Images that stayed <c>https:</c> links, so the file needs the network to show them.</param>
/// <param name="DiagramsRendered">Mermaid blocks replaced by the picture the reader was actually looking at.</param>
/// <param name="DiagramsAsSource">Mermaid blocks that exported as their own source text instead of as a diagram.</param>
/// <param name="FormulasAsSource">Formulas that exported as their own TeX source instead of as typeset maths.</param>
public sealed record HtmlExportResult(string Html, int ImagesInlined, int ImagesDropped, int ImagesLeftRemote,
                                      int DiagramsRendered, int DiagramsAsSource, int FormulasAsSource);

/// <summary>
/// Builds the single self-contained <c>.html</c> file behind "Export as HTML".
/// </summary>
/// <remarks>
/// <para><b>Self-contained.</b> The reader's stylesheet goes inside a <c>&lt;style&gt;</c> element and every local image
/// the document points at goes inside its own <c>src</c> as a <c>data:</c> URI, under the ceilings in
/// <see cref="HtmlExportOptions"/>. An image that can't travel loses its <c>src</c> and keeps its <c>alt</c>, so the
/// page shows the alternative text instead of a broken link to a virtual host that only exists inside this
/// application. Images the author wrote as <c>https:</c> stay <c>https:</c> — there is nothing on this machine to
/// carry — and the result says how many, because a file that needs the network is not quite the file that was asked
/// for.</para>
/// <para><b>The export is untrusted content, and there is only one sanitizer.</b> The HTML handed in is parsed by
/// <see cref="HtmlContentSanitizer"/> and written out by <see cref="SanitizedHtmlWriter"/> — the same two passes that
/// produce what the reader displays, with the same allow-lists. Nothing here assembles markup from document text, and
/// the only values this class writes into the DOM are <c>data:</c> URIs, whose media types come from a fixed list and
/// whose payload is base64, so they cannot contain a quote, a space or an angle bracket and cannot escape the
/// attribute they are written into. The writer then re-checks every URL it emits anyway
/// (<c>IsSafeUrlAttribute</c>: <c>https:</c> or <c>data:image/</c>, nothing else). The assembled page also carries a
/// CSP of its own that forbids script outright, which is a second answer to the same question in any browser that
/// opens it.</para>
/// <para><b>Task list checkboxes</b> are written with no task-line token, so they come out disabled: an exported page
/// has nothing to write a tick back to.</para>
/// </remarks>
public sealed class HtmlExporter
{
    /// <summary>
    /// What the exported page is allowed to do. There is no <c>script-src</c>, so <c>default-src 'none'</c> forbids
    /// script; <c>style-src 'unsafe-inline'</c> is what lets the one embedded stylesheet apply at all.
    /// </summary>
    internal const string ContentSecurityPolicy =
        "default-src 'none'; img-src data: https:; style-src 'unsafe-inline'; font-src data:; " +
        "connect-src 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";

    /// <summary>
    /// Undoes the parts of the reader's stylesheet that dress an application window rather than a document: a body
    /// exactly one viewport tall, with a scrolling element inside it. A file opened in a browser has no such element,
    /// so without this the page would show its first screen and nothing else — the failure that makes an export look
    /// like it worked. Appended after the app stylesheet so it wins, and it touches nothing else.
    /// </summary>
    internal const string LayoutOverrides = """
        html, body { height: auto; min-height: 100%; }
        body { display: block; overflow: visible; }
        """;

    private static readonly char[] SrcsetWhitespace = [' ', '\t', '\n', '\f', '\r'];

    private readonly IFileSystemProbe _fileSystem;
    private readonly IExportFileReader _reader;
    private readonly PathPolicy _policy;

    public HtmlExporter(IFileSystemProbe fileSystem, IExportFileReader reader, PathPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(reader);
        _fileSystem = fileSystem;
        _reader = reader;
        _policy = policy ?? PathPolicy.Current;
    }

    /// <summary>CPU-bound and file-system-bound: call from a thread-pool thread, never from the UI thread.</summary>
    public HtmlExportResult Build(HtmlExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var sanitizer = new HtmlContentSanitizer(new ResourceUrlRewriter(_fileSystem, _policy));
        using var document = sanitizer.SanitizeDom(request.BodyHtml, request.Context);
        var body = HtmlContentSanitizer.GetBody(document);

        var tally = new Tally();
        var html = string.Empty;
        if (body is not null)
        {
            ReplaceDiagrams(document, body, request, tally);
            RewriteImages(body, request, tally, cancellationToken);
            tally.FormulasAsSource = body.QuerySelectorAll("span.math, div.math").Length;
            html = SanitizedHtmlWriter.Write(body, long.MaxValue, taskLinePrefix: null,
                                             allowLocalLinks: request.Context.AllowLocalLinks, cancellationToken).Html;
        }

        return new HtmlExportResult(Assemble(html, request), tally.ImagesInlined, tally.ImagesDropped,
                                    tally.ImagesLeftRemote, tally.DiagramsRendered, tally.DiagramsAsSource,
                                    tally.FormulasAsSource);
    }

    private sealed class Tally
    {
        internal int ImagesInlined;
        internal int ImagesDropped;
        internal int ImagesLeftRemote;
        internal int DiagramsRendered;
        internal int DiagramsAsSource;
        internal int FormulasAsSource;
    }

    // ----- Diagrams -----

    /// <summary>
    /// Swaps each Mermaid block for the picture the page drew from it, when the page supplied one. The SVG arrives as
    /// markup and leaves as a base64 <c>data:image/svg+xml</c> in an <c>img</c>: an SVG loaded as an image document is
    /// inert — no script, no external fetch — which is the only footing on which markup a diagram engine built out of
    /// document text may be carried into a file someone else will open.
    /// </summary>
    private static void ReplaceDiagrams(IDocument document, IElement body, HtmlExportRequest request, Tally tally)
    {
        var blocks = body.QuerySelectorAll("pre.mermaid, div.mermaid");
        for (var i = 0; i < blocks.Length; i++)
        {
            var svg = i < request.DiagramSvg.Count ? request.DiagramSvg[i] : null;
            if (!IsUsableSvg(svg, request.Options.MaxDiagramChars) || blocks[i].Parent is not { } parent)
            {
                tally.DiagramsAsSource++;
                continue;
            }

            var image = document.CreateElement("img");
            image.SetAttribute("src", DataUri.From("image/svg+xml", Encoding.UTF8.GetBytes(svg!)));
            image.SetAttribute("alt", blocks[i].TextContent.Trim());
            image.SetAttribute("class", "mermaid");
            parent.ReplaceChild(image, blocks[i]);
            tally.DiagramsRendered++;
        }
    }

    /// <summary>A string we are willing to hand a browser as an image: SVG markup, and not an unbounded amount of it.</summary>
    private static bool IsUsableSvg(string? svg, int maxChars)
    {
        if (svg is null || svg.Length == 0 || svg.Length > maxChars)
        {
            return false;
        }

        var start = svg.AsSpan().TrimStart();
        return start.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
               || start.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase);
    }

    // ----- Images -----

    private void RewriteImages(IElement body, HtmlExportRequest request, Tally tally, CancellationToken cancellationToken)
    {
        long budget = request.Options.MaxTotalImageBytes;

        // <picture> is a set of alternatives for one <img>, and srcset is a set of alternatives for one src. An
        // exported file carries one picture, not a menu of them — and a data: URI contains the one character srcset
        // parses on, a comma, so a srcset full of them is a srcset no browser reads correctly. Both are therefore
        // resolved down to the img's own src here, and the alternatives are removed.
        foreach (var source in body.QuerySelectorAll("source").ToArray())
        {
            source.Parent?.RemoveChild(source);
        }

        foreach (var image in body.QuerySelectorAll("img").ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var src = image.GetAttribute("src");
            if (string.IsNullOrEmpty(src) && FirstSrcsetCandidate(image.GetAttribute("srcset")) is { } promoted)
            {
                src = promoted;
            }

            image.RemoveAttribute("srcset");
            image.RemoveAttribute("sizes");

            if (string.IsNullOrEmpty(src))
            {
                continue;
            }

            if (src.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                image.SetAttribute("src", src);   // already carried by the document itself
                tally.ImagesInlined++;
                continue;
            }

            if (ResolveDocHostPath(src, request.Context) is { } path)
            {
                var bytes = budget <= 0
                    ? null
                    : _reader.TryRead(path, Math.Min(request.Options.MaxImageBytes, budget));
                var mediaType = ExportMediaTypes.ForImage(IOPath.GetExtension(path.AsSpan()));
                if (bytes is null || mediaType is null)
                {
                    // Nothing that names this machine may survive into a file somebody else opens, so the reference
                    // goes and the alt text stays. A browser shows that; a dead doc.mdreader.example link shows a
                    // broken icon and tells the reader nothing.
                    image.RemoveAttribute("src");
                    tally.ImagesDropped++;
                    continue;
                }

                budget -= bytes.LongLength;
                image.SetAttribute("src", DataUri.From(mediaType, bytes));
                tally.ImagesInlined++;
                continue;
            }

            if (src.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            {
                image.SetAttribute("src", src);
                tally.ImagesLeftRemote++;
                continue;
            }

            // Anything else got here through a path the sanitizer doesn't produce. Drop it.
            image.RemoveAttribute("src");
            tally.ImagesDropped++;
        }
    }

    /// <summary>The first URL in a <c>srcset</c>, or null. Candidates are "url descriptor" pairs separated by commas.</summary>
    private static string? FirstSrcsetCandidate(string? srcset)
    {
        if (string.IsNullOrWhiteSpace(srcset))
        {
            return null;
        }

        foreach (var candidate in srcset.Split(','))
        {
            var url = candidate.Trim().Split(SrcsetWhitespace, 2, StringSplitOptions.RemoveEmptyEntries);
            if (url.Length > 0 && url[0].Length > 0)
            {
                return url[0];
            }
        }

        return null;
    }

    /// <summary>
    /// The file behind one of the application's own <c>doc.mdreader.example</c> URLs, or null when the URL names
    /// something else. The path is re-derived exactly the way <c>ResourceUrlRewriter</c> derived the URL — same
    /// parser, same resolver, same forbidden-path and inside-the-resource-root checks — so the export cannot read a
    /// file the reader would have refused to display.
    /// </summary>
    private string? ResolveDocHostPath(string url, RenderContext context)
    {
        if (!context.AllowLocalResources
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !IsDocHost(uri))
        {
            return null;
        }

        var reference = ReferenceParser.Parse(uri.AbsolutePath, _policy);
        if (reference.Kind != ReferenceKind.RootRelative)
        {
            return null;
        }

        var documentDirectory = _policy.GetDirectoryName(_policy.NormalizeFullPath(context.DocumentPath));
        var full = LocalPathResolver.Resolve(reference, documentDirectory ?? string.Empty, context.ResourceRoot, _policy);
        return full is null || _policy.IsForbiddenPath(full) || !_policy.IsWithin(full, context.ResourceRoot)
            ? null
            : full;
    }

    private static bool IsDocHost(Uri uri) =>
        uri.Host.AsSpan().TrimEnd('.').Equals(ProtocolConstants.DocHost, StringComparison.OrdinalIgnoreCase)
        || uri.IdnHost.AsSpan().TrimEnd('.').Equals(ProtocolConstants.DocHost, StringComparison.OrdinalIgnoreCase);

    // ----- The page itself -----

    private static string Assemble(string body, HtmlExportRequest request)
    {
        var theme = request.Theme == AppTheme.Dark ? "dark" : "light";
        var builder = new StringBuilder(body.Length + request.Stylesheet.Length + 2048);
        builder.Append("<!doctype html>\n<html lang=\"en\" data-theme=\"").Append(theme).Append("\">\n<head>\n")
               .Append("<meta charset=\"utf-8\">\n")
               .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
               .Append("<meta http-equiv=\"Content-Security-Policy\" content=\"").Append(ContentSecurityPolicy)
               .Append("\">\n")
               .Append("<meta name=\"generator\" content=\"MdReader\">\n")
               .Append("<title>");
        AppendEscaped(builder, request.Title);
        builder.Append("</title>\n");

        if (request.Stylesheet.Length > 0)
        {
            builder.Append("<style>\n").Append(request.Stylesheet).Append("\n</style>\n");
        }

        builder.Append("<style>\n").Append(LayoutOverrides).Append("\n</style>\n")
               .Append("</head>\n<body>\n<article class=\"markdown-body\">\n")
               .Append(body)
               .Append("\n</article>\n</body>\n</html>\n");
        return builder.ToString();
    }

    /// <summary><c>title</c> is RCDATA: <c>&amp;</c> and <c>&lt;</c> end something, so both are escaped, and so is
    /// <c>&gt;</c> for good measure.</summary>
    private static void AppendEscaped(StringBuilder builder, string text)
    {
        foreach (var c in text)
        {
            switch (c)
            {
                case '&': builder.Append("&amp;"); break;
                case '<': builder.Append("&lt;"); break;
                case '>': builder.Append("&gt;"); break;
                default: builder.Append(c); break;
            }
        }
    }
}
