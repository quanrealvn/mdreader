using System.Text.Json.Serialization;

namespace MdReader.Core.Rendering;

public interface IMarkdownRenderer
{
    /// Parses, renders and ALWAYS sanitizes. CPU-bound: call from a thread-pool thread.
    /// Thread-safe (pipeline is immutable; per-call HtmlSanitizer instance).
    /// Throws OperationCanceledException (checked between phases). Any other exception is a bug.
    RenderResult Render(string markdown, RenderContext context, CancellationToken cancellationToken = default);
}

/// DocumentPath: absolute, normalized. ResourceRoot: absolute folder mapped to https://doc.mdreader.example/.
public sealed record RenderContext(string DocumentPath, string ResourceRoot)
{
    private readonly bool _allowLocalResources = true;
    private readonly Uri? _baseUrl;

    /// <summary>The document's folder, per the running platform. Path resolution for author-written references does
    /// not use it: that goes through the <see cref="Paths.PathPolicy"/> the sanitizer was given.</summary>
    public string DocumentDirectory => Path.GetDirectoryName(DocumentPath)!;

    /// false (web version, §15): every local image reference (relative, root-relative, Windows absolute, file:, UNC,
    /// author-written doc-host URL) is dropped without any path resolution or file-system call; https and data:image/*
    /// images behave as before. The paths are then not resolved at all (safe on Linux): DocumentPath only supplies the
    /// fallback title (its file name), ResourceRoot is unused.
    /// <para>Always false when <see cref="BaseUrl"/> is set: a document fetched from the web may never reach this
    /// machine's file system, and that holds by construction rather than by every caller remembering it.</para>
    public bool AllowLocalResources
    {
        get => _allowLocalResources && _baseUrl is null;
        init => _allowLocalResources = value;
    }

    /// <summary>
    /// The http(s) address a document was fetched from (after any redirects), or null for a document on this machine
    /// (§8). When it is set, relative, root-relative and protocol-relative image and link URLs resolve against it —
    /// as they would in a browser — and every reference naming a local file is dropped instead.
    /// </summary>
    public Uri? BaseUrl
    {
        get => _baseUrl;
        init
        {
            if (value is not null && !IsWebUrl(value))
            {
                throw new ArgumentException("A render context's base URL must be an absolute http(s) URL.", nameof(value));
            }

            _baseUrl = value;
        }
    }

    /// <summary>
    /// Whether a link in this document may name this machine (<c>file:</c>, <c>C:\…</c>, <c>\\server\…</c>). False
    /// exactly when the document came from the web. This is the *one* place that decision is made: the sanitizer's
    /// URL filter and its writer both read it, so the two layers cannot end up disagreeing about the same document.
    /// </summary>
    internal bool AllowLocalLinks => BaseUrl is null;

    /// <summary>The context for a document fetched from <paramref name="url"/>.</summary>
    public static RenderContext ForRemote(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!IsWebUrl(url))
        {
            throw new ArgumentException("A remote document's URL must be an absolute http(s) URL.", nameof(url));
        }

        // ResourceRoot is unused with a base URL; DocumentPath only supplies the fallback title's file name.
        return new RenderContext(url.AbsoluteUri, string.Empty) { BaseUrl = url };
    }

    /// <summary>
    /// A ceiling on the HTML elements the sanitizer may build, applied on top of the size-derived budget
    /// (<c>RenderLimits.ElementBudget</c>); a document needing more is shown as plain text. Null (the desktop shells)
    /// keeps the size-derived budget alone, which for a multi-megabyte document permits a DOM of several hundred
    /// megabytes — affordable on a desktop, fatal in the web version's 512 MB container, which therefore sets a cap.
    /// </summary>
    public int? MaxElements { get; init; }

    private static bool IsWebUrl(Uri url) =>
        url.IsAbsoluteUri
        && (url.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || url.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));
}

public sealed record RenderResult(
    string Html,                       // sanitized; safe to assign to innerHTML
    IReadOnlyList<int> BlockOffsets,   // ascending char offsets in Html where each top-level node starts ([] if Html is empty)
    IReadOnlyList<TocEntry> Toc,
    string Title,                      // plain text of first h1, else file name incl. extension ("README.md")
    RenderFeatures Features,
    RenderTimings Timings);

public sealed record TocEntry(int Level, string Id, string Text)   // Id == the id attribute in Html
{
    /// <summary>
    /// 1-based line of the heading in the Markdown source, or 0 when it isn't known. This is the shell's only
    /// source-line map into a rendered document: a cross-file search hit on line N scrolls to the last heading
    /// whose <see cref="Line"/> is &lt;= N (M13). Deliberately kept off the wire — the page has no use for it, and
    /// the protocol payload of a large document is expensive enough already (§7.2).
    /// </summary>
    [JsonIgnore]
    public int Line { get; init; }
}
public sealed record RenderFeatures(bool Mermaid, bool Math, bool Code);
public sealed record RenderTimings(double ParseMs, double HtmlMs, double SanitizeMs, double TotalMs);
