namespace MdReader.Core.Rendering;

/// <summary>
/// Resource limits against hostile documents (M2 and final security reviews). A document that exceeds one of them is
/// shown as escaped plain text (<see cref="MarkdownRenderer"/>'s fallback) instead of costing minutes of CPU or
/// gigabytes of memory on a render thread that can't be interrupted. Legitimate documents stay far below every limit.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item><see cref="MaxMarkdownNesting"/>: checked on the Markdown before Markdig sees it.</item>
/// <item><see cref="MaxAutolinkCandidatesPerBlock"/>: above it, Markdig runs without the bare-autolink extension.</item>
/// <item><see cref="MarkdownHtmlBudget"/>: enforced while Markdig writes its HTML (<see cref="BudgetedHtmlWriter"/>).</item>
/// <item><see cref="MaxHtmlNesting"/>: checked on Markdig's HTML before AngleSharp parses it
/// (<c>Sanitization.HtmlNestingScanner</c>).</item>
/// <item><see cref="ElementBudget"/> and <see cref="OutputBudget"/>: enforced while AngleSharp builds the DOM and while
/// the writer serializes it (<c>Sanitization.ElementBudget</c>, <c>SanitizedHtmlWriter</c>).</item>
/// </list>
/// Every budget is derived from the length of the <em>Markdown</em> the user opened, so HTML that an earlier stage
/// amplified never earns a proportionally bigger budget downstream.
/// </remarks>
internal static class RenderLimits
{
    /// <summary>
    /// Container nesting (block quotes, lists, definition lists) that any single line may imply, estimated from its
    /// prefix as <c>markers + indentation columns / 2</c>. Markdig's block parsing is roughly cubic in nesting depth
    /// (1,500 levels: 4.7 s, 3,000 levels: 35 s, not cancellable) and it rejects more than 128 levels
    /// (<c>MaximumNestingDepth</c>) anyway, but only after paying that cost.
    /// </summary>
    /// <remarks>
    /// Only a line that contains a container marker can open a nesting level (indentation alone just continues the
    /// containers opened on earlier lines), and on such a line every enclosing list level needs at least two columns of
    /// indentation or a marker. So the estimate is an upper bound on the real depth, and lines without a marker are
    /// ignored: whitespace-only lines and deeply indented code no longer count. Thematic breaks (<c>* * * …</c>) are not
    /// list markers. A document Markdig accepts (at most 128 levels) estimates at most ~192. Remaining false positive: a
    /// line that starts with a list or quote marker after more than 512 columns of indentation (e.g. inside a code block).
    /// </remarks>
    public const int MaxMarkdownNesting = 256;

    /// <summary>
    /// Bare-autolink candidates (<c>www.</c>, <c>://</c>, <c>mailto:</c>) in one block (text between blank lines).
    /// Markdig's autolink extension is quadratic within a paragraph (40,000 <c>www.a.b</c> in one paragraph: 5.6 s,
    /// not cancellable; 2,000: ~36 ms). Above this count the document is rendered without that extension: explicit links
    /// and <c>&lt;https://…&gt;</c> autolinks still work, bare URLs stay text.
    /// </summary>
    public const int MaxAutolinkCandidatesPerBlock = 2_000;

    /// <summary>
    /// Element nesting depth of the HTML handed to the sanitizer (browsers stop nesting at 512). AngleSharp's tree
    /// construction is quadratic in depth because scope checks walk the stack of open elements: 40,000 nested
    /// <c>div</c>s take ~3.6 s to parse, a million would take hours. Raw HTML is the only source of such depth; Markdig's
    /// own structure is limited to 128 levels.
    /// </summary>
    public const int MaxHtmlNesting = 512;

    /// <summary>
    /// Characters Markdig may write for <paramref name="markdownLength"/> characters of Markdown. Reference links repeat
    /// their destination: <c>[a]: https://example.com/&lt;10,000 chars&gt;</c> followed by <c>[a] [a] …</c> turned 102 KB into
    /// 51 MB of HTML and 2.7 GB of memory. Legitimate documents stay far below: measured worst cases are ~16× (a line of
    /// repeated footnote references, thousands of empty headings), ~13× (dense aligned tables), ~10× (task lists), ~2× for
    /// typical prose (the 5 MB perf document).
    /// </summary>
    public static long MarkdownHtmlBudget(int markdownLength) => (24L * markdownLength) + 1_048_576;

    /// <summary>
    /// HTML elements AngleSharp may create for <paramref name="htmlLength"/> characters of HTML rendered from
    /// <paramref name="sourceLength"/> characters of Markdown. Every element written in the HTML costs at least 3
    /// characters (<c>&lt;b&gt;</c>), and Markdown produces at most about one element per character (measured maximum
    /// 0.75: repeated footnote references), so legitimate input stays below both terms. The HTML parser's "reconstruct
    /// the active formatting elements" step, however, re-opens (clones) every open formatting element at each new block,
    /// and the Noah's-Ark clause only de-duplicates identical tag + attributes:
    /// <c>&lt;div&gt;&lt;b title=0&gt;…&lt;b title=199&gt;&lt;/div&gt;</c> followed by N <c>&lt;div&gt;x&lt;/div&gt;</c>
    /// creates 200·N elements (242 KB of input → 74 MB of HTML, 2.5 GB peak memory before this limit).
    /// </summary>
    public static long ElementBudget(int htmlLength, int sourceLength) => Math.Min(htmlLength / 2, sourceLength) + 10_000L;

    /// <summary>
    /// Characters the writer may produce. Cloned elements share their attribute strings in the DOM but serialize them
    /// again each time, so the element budget alone doesn't bound the output. Legitimate output is at most ~6× its HTML
    /// input (a raw U+00A0 or <c>"</c> becomes <c>&amp;nbsp;</c> / <c>&amp;quot;</c>) and never more than the Markdig
    /// output budget allows for its Markdown.
    /// </summary>
    public static long OutputBudget(int htmlLength, int sourceLength) =>
        Math.Min((8L * htmlLength) + 65_536, 2 * MarkdownHtmlBudget(sourceLength));

    /// <summary>True if some line's container prefix implies more than <see cref="MaxMarkdownNesting"/> levels.</summary>
    public static bool ExceedsMarkdownNesting(string markdown) => EstimateMarkdownNesting(markdown, MaxMarkdownNesting) > MaxMarkdownNesting;

    /// <summary>
    /// The largest nesting estimate over all lines that contain a container marker (stops early once
    /// <paramref name="stopAbove"/> is exceeded). A line's prefix is scanned for spaces/tabs (columns), <c>&gt;</c>, list
    /// markers (<c>-</c>, <c>*</c>, <c>+</c>, or 1–9 digits followed by <c>.</c>/<c>)</c>) and definition markers
    /// (<c>:</c>), the latter two followed by a space, a tab or the line end.
    /// </summary>
    internal static int EstimateMarkdownNesting(string markdown, int stopAbove = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var text = markdown.AsSpan();
        var max = 0;
        var position = 0;
        while (position < text.Length)
        {
            var columns = 0;
            var markers = 0;
            var i = position;
            var thematicBreakStart = ThematicBreakStart(text, position);
            while (i < text.Length)
            {
                var c = text[i];
                if (c == ' ')
                {
                    columns++;
                    i++;
                }
                else if (c == '\t')
                {
                    columns += 4 - (columns % 4);
                    i++;
                }
                else if (c == '>')
                {
                    markers++;
                    i++;
                }
                else if (c is '-' or '*' or '+' or ':' && IsMarkerEnd(text, i + 1) && !(c != ':' && i >= thematicBreakStart))
                {
                    markers++;
                    i++;
                }
                else if (char.IsAsciiDigit(c) && TryOrderedMarker(text, i, out var markerEnd))
                {
                    markers++;
                    i = markerEnd;
                }
                else
                {
                    break;
                }
            }

            if (markers > 0)
            {
                var estimate = markers + (columns / 2);
                if (estimate > max)
                {
                    max = estimate;
                    if (max > stopAbove)
                    {
                        return max;
                    }
                }
            }

            var lineEnd = text[i..].IndexOfAny('\n', '\r');
            if (lineEnd < 0)
            {
                break;
            }

            position = i + lineEnd + 1;
        }

        return max;
    }

    /// <summary>
    /// True if some block (text between blank lines) contains more than <see cref="MaxAutolinkCandidatesPerBlock"/>
    /// bare-autolink candidates.
    /// </summary>
    public static bool ExceedsAutolinkCandidates(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        // Cheap global count first: almost every document has fewer candidates in total than one block may have.
        return CountCandidates(markdown) > MaxAutolinkCandidatesPerBlock
               && MaxAutolinkCandidatesInABlock(markdown, MaxAutolinkCandidatesPerBlock) > MaxAutolinkCandidatesPerBlock;
    }

    /// <summary>
    /// The largest number of candidates in one block (stops early once <paramref name="stopAbove"/> is exceeded).
    /// Over-counts on purpose (candidates inside code or explicit link destinations count too).
    /// </summary>
    internal static int MaxAutolinkCandidatesInABlock(string markdown, int stopAbove = int.MaxValue)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var text = markdown.AsSpan();
        var max = 0;
        var block = 0;
        while (!text.IsEmpty)
        {
            var lineEnd = text.IndexOf('\n');
            var line = lineEnd < 0 ? text : text[..lineEnd];
            if (line.IsWhiteSpace())
            {
                block = 0;
            }
            else
            {
                block += CountCandidates(line);
                if (block > max)
                {
                    max = block;
                    if (max > stopAbove)
                    {
                        return max;
                    }
                }
            }

            text = lineEnd < 0 ? default : text[(lineEnd + 1)..];
        }

        return max;
    }

    private static int CountCandidates(ReadOnlySpan<char> text) =>
        Count(text, "www.", StringComparison.OrdinalIgnoreCase) + Count(text, "://", StringComparison.Ordinal)
        + Count(text, "mailto:", StringComparison.OrdinalIgnoreCase);

    private static int Count(ReadOnlySpan<char> text, string value, StringComparison comparison)
    {
        var count = 0;
        while (true)
        {
            var index = text.IndexOf(value, comparison);
            if (index < 0)
            {
                return count;
            }

            count++;
            text = text[(index + value.Length)..];
        }
    }

    private static bool IsMarkerEnd(ReadOnlySpan<char> text, int index) =>
        index >= text.Length || text[index] is ' ' or '\t' or '\n' or '\r';

    /// <summary>
    /// A thematic break: three or more of the same <c>*</c>, <c>-</c> or <c>_</c>, optionally separated by spaces or
    /// tabs, and nothing else on the line. It takes precedence over a list item.
    /// </summary>
    private static int ThematicBreakStart(ReadOnlySpan<char> text, int lineStart)
    {
        // Scanned once per line from its end: from the returned index, the rest of the line is three or more of one
        // marker character (plus spaces/tabs). int.MaxValue if the line doesn't end in a thematic break.
        var lineEnd = text[lineStart..].IndexOfAny('\n', '\r');
        var line = lineEnd < 0 ? text[lineStart..] : text.Slice(lineStart, lineEnd);
        var marker = '\0';
        var count = 0;
        var start = int.MaxValue;
        for (var k = line.Length - 1; k >= 0; k--)
        {
            var c = line[k];
            if (c is ' ' or '\t')
            {
                continue;
            }

            if (marker == '\0' && c is '-' or '*' or '_')
            {
                marker = c;
            }

            if (c != marker)
            {
                break;
            }

            count++;
            start = lineStart + k;
        }

        return count >= 3 ? start : int.MaxValue;
    }

    private static bool TryOrderedMarker(ReadOnlySpan<char> text, int start, out int end)
    {
        var i = start;
        while (i < text.Length && i - start < 9 && char.IsAsciiDigit(text[i]))
        {
            i++;
        }

        end = i + 1;
        return i < text.Length && text[i] is '.' or ')' && IsMarkerEnd(text, i + 1);
    }
}

/// <summary>Thrown when a document exceeds one of the <see cref="RenderLimits"/>; caught by <see cref="MarkdownRenderer"/>.</summary>
internal sealed class RenderLimitExceededException : Exception
{
    public RenderLimitExceededException(string message)
        : base(message)
    {
    }
}
