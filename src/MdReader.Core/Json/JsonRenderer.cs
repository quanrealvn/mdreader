using System.Diagnostics;
using System.Text;
using MdReader.Core.Rendering;

namespace MdReader.Core.Json;

/// <summary>Which of the three views a render produced.</summary>
public enum JsonRenderOutcome
{
    /// <summary>The document was read and is shown as a collapsible tree.</summary>
    Tree,

    /// <summary>Valid JSON, but past one of the budgets: its own text is shown, with a line saying why.</summary>
    Source,

    /// <summary>Not JSON: the error is shown with a caret under the offending character, above the document's text.</summary>
    Error,
}

/// <summary>
/// The result of reading one JSON document.
/// </summary>
/// <param name="Html">Ready to assign to <c>innerHTML</c>: every character of the document is in it as text.</param>
/// <param name="BlockOffsets">Character offsets of the top-level nodes, as <c>RenderResult</c> defines them. The whole
/// view is one element, so this is <c>[0]</c>.</param>
/// <param name="Toc">One entry per key of a top-level object, in document order; empty for any other shape.</param>
/// <param name="Outcome">Which view <see cref="Html"/> holds.</param>
/// <param name="Error">Where the document stopped being JSON, when <see cref="Outcome"/> is
/// <see cref="JsonRenderOutcome.Error"/>; null otherwise.</param>
public sealed record JsonRenderResult(
    string Html,
    IReadOnlyList<int> BlockOffsets,
    IReadOnlyList<TocEntry> Toc,
    JsonRenderOutcome Outcome,
    JsonParseError? Error,
    RenderTimings Timings)
{
    /// <summary>
    /// The same result in the shape the shells and the web app already pass around, so that wiring JSON into the
    /// render path is a mapping and not a new contract. A JSON document has no diagrams, no maths and no code blocks,
    /// so its features are all false, and nothing was sanitized, so that timing is zero (see the class remarks).
    /// </summary>
    /// <param name="title">Usually the document's file name, as <c>RenderResult.Title</c> defines it.</param>
    public RenderResult ToRenderResult(string title) =>
        new(Html, BlockOffsets, Toc, title, new RenderFeatures(false, false, false), Timings);
}

/// <summary>
/// JSON → HTML (JSON-PLAN). The entry point for phase 1: tree view, errors with a line, a column and a caret, and
/// budgets that bound a hostile document.
/// </summary>
/// <remarks>
/// <para><strong>The output is not passed through the HTML sanitizer, and that is a decision rather than an
/// oversight.</strong> The sanitizer exists because a Markdown document may contain raw author HTML, so the markup
/// reaching the page is partly written by the document. Nothing of the sort happens here: every tag, attribute name
/// and attribute value in the output is a literal in this assembly or an ordinal of our own making, and every
/// author-controlled character — keys, string values, number tokens, the raw source in the fallback views — reaches
/// the page as a text node through <see cref="JsonText"/>. There is no path by which a document can contribute
/// markup, so there is nothing for an allow-list to allow.</para>
/// <para>Running it anyway would cost an AngleSharp parse and re-serialization of everything written here, and would
/// <em>hide</em> the only kind of bug it could catch — an emitter that writes an attribute it should not — instead of
/// failing the build. So the property is proved rather than enforced at runtime: <c>JsonSecurityTests</c> asserts
/// that <c>Sanitize(html) == html</c>, character for character, over the whole corpus including the XSS vectors. If
/// a future change breaks it, that test fails; and because sanitizing is thereby a no-op, a caller that does route
/// this HTML through the sanitizer anyway loses nothing but time.</para>
/// <para>The page's other two layers are unchanged: the CSP forbids inline script and <c>eval</c> whatever the HTML
/// says, and navigation is locked down (ARCHITECTURE §8.1). This is not the only thing standing between a hostile
/// document and the reader.</para>
/// <para>Thread-safe and allocation-bounded: no shared state, and every stage carries the caller's cancellation token.
/// CPU-bound, so call it from a thread-pool thread as the Markdown renderer requires.</para>
/// </remarks>
public static class JsonRenderer
{
    /// <summary>U+FEFF: an encoding artefact at the start of the text, not a character of the document.</summary>
    private const char ByteOrderMark = (char)0xFEFF;

    public static JsonRenderResult Render(
        string json,
        JsonRenderOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);
        options ??= JsonRenderOptions.Default;

        var startTimestamp = Stopwatch.GetTimestamp();
        cancellationToken.ThrowIfCancellationRequested();

        // A byte-order mark is an encoding artefact, not a character of the document; System.Text.Json refuses one.
        var source = json.StartsWith(ByteOrderMark) ? json[1..] : json;
        var utf8 = Encoding.UTF8.GetBytes(source);

        var scan = JsonScanner.Scan(utf8, options, cancellationToken);
        var parseTime = Stopwatch.GetElapsedTime(startTimestamp);
        cancellationToken.ThrowIfCancellationRequested();

        var htmlStart = Stopwatch.GetTimestamp();
        var html = new StringBuilder(EstimateCapacity(source.Length));
        html.Append("<div class=\"json-doc\">");
        var treeStart = html.Length;

        var toc = (IReadOnlyList<TocEntry>)[];
        var notice = scan.Notice;
        var outcome = JsonRenderOutcome.Tree;

        if (scan.Error is null && notice is null)
        {
            try
            {
                toc = JsonTreeWriter.Write(html, utf8, scan, options, cancellationToken);
            }
            catch (JsonScanner.TooBigException exception)
            {
                // Nothing written so far is kept: the reader gets one view, not a tree that stops in the middle.
                html.Length = treeStart;
                toc = [];
                notice = exception.Notice;
            }
        }

        if (scan.Error is not null)
        {
            WriteError(html, scan.Error, source);
            outcome = JsonRenderOutcome.Error;
        }
        else if (notice is not null)
        {
            WriteSource(html, notice, source);
            outcome = JsonRenderOutcome.Source;
        }

        html.Append("</div>");
        var htmlTime = Stopwatch.GetElapsedTime(htmlStart);
        cancellationToken.ThrowIfCancellationRequested();

        var timings = new RenderTimings(
            parseTime.TotalMilliseconds,
            htmlTime.TotalMilliseconds,
            0,
            Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);

        return new JsonRenderResult(html.ToString(), [0], toc, outcome, scan.Error, timings);
    }

    /// <summary>
    /// The error, then the document's own text underneath it. Being told where the file stops being JSON is only
    /// half of it; the other half is seeing the line.
    /// </summary>
    private static void WriteError(StringBuilder html, JsonParseError error, string source)
    {
        html.Append("<p class=\"json-error-summary\">");
        JsonText.AppendHtml(html, error.Summary);
        html.Append("</p>");

        var context = error.Excerpt + "\n" + error.Caret;
        JsonText.OpenPre(html, "json-error-context", context);
        JsonText.AppendHtml(html, context);
        html.Append("</pre>");

        WriteSourceText(html, source);
    }

    private static void WriteSource(StringBuilder html, string notice, string source)
    {
        html.Append("<p class=\"json-notice\">");
        JsonText.AppendHtml(html, notice);
        html.Append("</p>");
        WriteSourceText(html, source);
    }

    private static void WriteSourceText(StringBuilder html, string source)
    {
        JsonText.OpenPre(html, "json-source", source);
        JsonText.AppendSourceText(html, source);
        html.Append("</pre>");
    }

    /// <summary>
    /// Room for a typical tree without repeated doubling. Bounded so that a large document does not reserve a
    /// multiple of itself before a single row has been written.
    /// </summary>
    private static int EstimateCapacity(int sourceLength) => (int)Math.Min(4L * sourceLength + 1_024, 8 * 1024 * 1024);
}
