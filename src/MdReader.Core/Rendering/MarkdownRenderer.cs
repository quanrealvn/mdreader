using System.Diagnostics;
using System.Text;
using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.Yaml;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdReader.Core.Paths;
using MdReader.Core.Rendering.Sanitization;

namespace MdReader.Core.Rendering;

/// <summary>
/// Markdown → sanitized HTML (ARCHITECTURE §4.1). This is the only way HTML leaves Core: the pipeline factory, sanitizer
/// and writer are internal, so nothing can skip sanitization. Thread-safe: the pipeline is immutable and every call
/// creates its own HtmlSanitizer.
/// </summary>
public sealed class MarkdownRenderer : IMarkdownRenderer
{
    private const string MermaidInfo = "mermaid";

    private readonly HtmlContentSanitizer _sanitizer;

    public MarkdownRenderer(IFileSystemProbe fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _sanitizer = new HtmlContentSanitizer(new ResourceUrlRewriter(fileSystem));
    }

    public RenderResult Render(string markdown, RenderContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(context);

        var startTimestamp = Stopwatch.GetTimestamp();
        cancellationToken.ThrowIfCancellationRequested();

        // Bare autolinks are quadratic within a paragraph in Markdig: a document with too many candidates in one block
        // is rendered without them (explicit links still work).
        var pipeline = RenderLimits.ExceedsAutolinkCandidates(markdown)
            ? MarkdownPipelineFactory.PipelineWithoutAutoLinks
            : MarkdownPipelineFactory.Pipeline;

        // Documents that exceed a resource limit (RenderLimits) or Markdig's own nesting limit can't be rendered as
        // Markdown, but they are still shown: as escaped plain text (empty TOC, file-name title, no features).
        DocumentOutline outline;
        string html;
        TimeSpan parseTime;
        TimeSpan htmlTime;
        if (RenderLimits.ExceedsMarkdownNesting(markdown))
        {
            // Markdig's block parsing is ~cubic in nesting depth and can't be interrupted: don't let it start.
            (outline, html, parseTime, htmlTime) = PlainTextFallback(markdown, startTimestamp);
        }
        else
        {
            try
            {
                // 1. Parse + 2. AST pass (table alignment, features, TOC, title).
                var document = Markdown.Parse(markdown, pipeline);
                cancellationToken.ThrowIfCancellationRequested();
                outline = Analyze(document, cancellationToken);
                parseTime = Stopwatch.GetElapsedTime(startTimestamp);
                cancellationToken.ThrowIfCancellationRequested();

                // 3. HTML, into a writer that stops Markdig's own amplification (budget based on the Markdown length).
                var htmlStart = Stopwatch.GetTimestamp();
                var writer = new BudgetedHtmlWriter(
                    RenderLimits.MarkdownHtmlBudget(markdown.Length),
                    (int)Math.Min(2L * markdown.Length, 64L * 1024 * 1024));
                Markdown.ToHtml(document, writer, pipeline);
                html = writer.ToString();
                htmlTime = Stopwatch.GetElapsedTime(htmlStart);
            }
            catch (ArgumentException ex) when (IsMarkdigContentLimit(ex))
            {
                // Markdig rejects nesting deeper than MaximumNestingDepth (128) while parsing or rendering.
                (outline, html, parseTime, htmlTime) = PlainTextFallback(markdown, startTimestamp);
            }
            catch (RenderLimitExceededException)
            {
                // Markdig's HTML outgrew its budget.
                (outline, html, parseTime, htmlTime) = PlainTextFallback(markdown, startTimestamp);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 4. Sanitize (always). Its budgets are based on the Markdown length too, so HTML amplified by Markdig doesn't
        //    earn a bigger budget downstream.
        var sanitizeStart = Stopwatch.GetTimestamp();
        SanitizedHtml sanitized;
        try
        {
            sanitized = _sanitizer.Sanitize(html, context, markdown.Length, cancellationToken);
        }
        catch (RenderLimitExceededException)
        {
            // HTML nesting depth, element budget or output budget exceeded (raw-HTML amplification).
            outline = DocumentOutline.Empty;
            html = RenderAsPlainText(markdown);
            sanitized = _sanitizer.Sanitize(html, context, markdown.Length, cancellationToken);
        }

        var sanitizeTime = Stopwatch.GetElapsedTime(sanitizeStart);
        cancellationToken.ThrowIfCancellationRequested();

        var title = outline.Title ?? Path.GetFileName(context.DocumentPath);
        var timings = new RenderTimings(
            parseTime.TotalMilliseconds,
            htmlTime.TotalMilliseconds,
            sanitizeTime.TotalMilliseconds,
            Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);

        return new RenderResult(sanitized.Html, sanitized.BlockOffsets, outline.Toc, title, outline.Features, timings);
    }

    /// <summary>
    /// One walk over the block tree: moves table alignment into <c>align</c> attributes, detects features, collects the
    /// TOC and the title (plain text of the first non-empty h1).
    /// </summary>
    internal static DocumentOutline Analyze(MarkdownDocument document, CancellationToken cancellationToken = default)
    {
        var state = new AnalysisState(cancellationToken);
        state.Visit(document);
        return new DocumentOutline(state.Toc, state.Title, new RenderFeatures(state.Mermaid, state.Math, state.Code));
    }

    private static (DocumentOutline Outline, string Html, TimeSpan ParseTime, TimeSpan HtmlTime) PlainTextFallback(
        string markdown, long startTimestamp)
    {
        var fallbackStart = Stopwatch.GetTimestamp();
        var html = RenderAsPlainText(markdown);
        return (DocumentOutline.Empty, html, Stopwatch.GetElapsedTime(startTimestamp, fallbackStart), Stopwatch.GetElapsedTime(fallbackStart));
    }

    /// <summary>Escaped source in a <c>pre</c> (the leading newline protects a first line that starts with one).</summary>
    internal static string RenderAsPlainText(string markdown)
    {
        var builder = new StringBuilder(markdown.Length + 16);
        builder.Append("<pre>\n");
        foreach (var c in markdown)
        {
            switch (c)
            {
                case '&':
                    builder.Append("&amp;");
                    break;
                case '<':
                    builder.Append("&lt;");
                    break;
                case '>':
                    builder.Append("&gt;");
                    break;
                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.Append("</pre>\n").ToString();
    }

    /// <summary>An <see cref="ArgumentException"/> thrown by Markdig itself (its depth limit), not by argument validation of ours.</summary>
    private static bool IsMarkdigContentLimit(ArgumentException exception) =>
        exception.TargetSite?.DeclaringType?.Assembly == typeof(Markdown).Assembly;

    /// <summary>Result of the AST pass.</summary>
    internal sealed record DocumentOutline(IReadOnlyList<TocEntry> Toc, string? Title, RenderFeatures Features)
    {
        public static DocumentOutline Empty { get; } = new([], null, new RenderFeatures(false, false, false));
    }

    private sealed class AnalysisState(CancellationToken cancellationToken)
    {
        private int _blockCount;

        public List<TocEntry> Toc { get; } = [];
        public string? Title { get; private set; }
        public bool Mermaid { get; private set; }
        public bool Math { get; private set; }
        public bool Code { get; private set; }

        /// <summary>Pre-order walk (document order). Markdig caps nesting depth, so recursion is bounded.</summary>
        public void Visit(Block block)
        {
            if (++_blockCount % 4096 == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            switch (block)
            {
                case Table table:
                    MoveAlignmentToAttributes(table);
                    break;
                case HeadingBlock heading:
                    VisitHeading(heading);
                    break;
                case MathBlock:                                  // derives from FencedCodeBlock: test it first
                    Math = true;
                    break;
                case YamlFrontMatterBlock:                       // a CodeBlock that is never rendered
                    break;
                case FencedCodeBlock fenced when string.Equals(fenced.Info, MermaidInfo, StringComparison.OrdinalIgnoreCase):
                    Mermaid = true;
                    break;
                case CodeBlock:
                    Code = true;
                    break;
            }

            if (!Math && block is LeafBlock { Inline: { } inline } && ContainsMath(inline))
            {
                Math = true;
            }

            if (block is ContainerBlock container)
            {
                for (var i = 0; i < container.Count; i++)
                {
                    Visit(container[i]);
                }
            }
        }

        private void VisitHeading(HeadingBlock heading)
        {
            // The id travels in the TOC message too: cap it like the text. Done before the HTML is written, so the id
            // attribute and TocEntry.Id stay equal.
            if (heading.TryGetAttributes() is { Id: { Length: > TocExtractor.MaxTextLength } longId } attributes)
            {
                attributes.Id = TocExtractor.Truncate(longId, TocExtractor.MaxTextLength);
            }

            var text = TocExtractor.GetPlainText(heading);
            if (Title is null && heading.Level == 1 && text.Length > 0)
            {
                Title = text;
            }

            if (TocExtractor.CreateEntry(heading, text) is { } entry)
            {
                Toc.Add(entry);
            }
        }

        private static bool ContainsMath(ContainerInline root)
        {
            // Iterative: Markdig enforces its nesting limit for emphasis only when rendering, so right after parsing
            // an inline tree can be ~100 000 levels deep.
            foreach (var inline in TocExtractor.EnumerateInlines(root))
            {
                if (inline is MathInline)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Markdig would emit <c>style="text-align: …"</c>, which the sanitizer strips; GitHub emits <c>align</c>. The
        /// column index logic mirrors Markdig's HtmlTableRenderer.
        /// </summary>
        private static void MoveAlignmentToAttributes(Table table)
        {
            var columns = table.ColumnDefinitions;
            if (columns.Count == 0)
            {
                return;
            }

            foreach (var rowBlock in table)
            {
                if (rowBlock is not TableRow row)
                {
                    continue;
                }

                for (var i = 0; i < row.Count; i++)
                {
                    if (row[i] is not TableCell cell)
                    {
                        continue;
                    }

                    var columnIndex = cell.ColumnIndex < 0 || cell.ColumnIndex >= columns.Count ? i : cell.ColumnIndex;
                    columnIndex = System.Math.Min(columnIndex, columns.Count - 1);
                    var align = columns[columnIndex].Alignment switch
                    {
                        TableColumnAlign.Left => "left",
                        TableColumnAlign.Center => "center",
                        TableColumnAlign.Right => "right",
                        _ => null,
                    };

                    if (align is not null)
                    {
                        cell.GetAttributes().AddProperty("align", align);
                    }
                }
            }

            foreach (var column in columns)
            {
                column.Alignment = null;
            }
        }
    }
}
