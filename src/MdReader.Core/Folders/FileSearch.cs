using MdReader.Core.Documents;

namespace MdReader.Core.Folders;

/// <summary>What to look for across a set of files (ARCHITECTURE M13).</summary>
public sealed record SearchQuery(string Text)
{
    public const int DefaultMaxHitsPerFile = 200;
    public const int DefaultMaxHits = 5_000;

    public bool MatchCase { get; init; }

    /// <summary>After this many hits in one file the rest of that file is skipped.</summary>
    public int MaxHitsPerFile { get; init; } = DefaultMaxHitsPerFile;

    /// <summary>After this many hits in total the search stops and reports itself truncated.</summary>
    public int MaxHits { get; init; } = DefaultMaxHits;

    internal StringComparison Comparison => MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    internal bool IsUsable => !string.IsNullOrEmpty(Text);
}

/// <summary>
/// One match. <paramref name="Line"/> is 1-based; <paramref name="Context"/> is the line the match sits on, cut
/// down to <see cref="FileSearch.MaxContextChars"/> around the match, and <paramref name="MatchStart"/> is the
/// offset of the match *inside that context*, not inside the original line.
/// </summary>
public sealed record SearchHit(string FilePath, int Line, string Context, int MatchStart, int MatchLength);

/// <param name="FilesScanned">Files that were read (whether or not they held the term).</param>
/// <param name="FilesWithHits">Files that held the term — the number of groups the results panel shows.</param>
/// <param name="FilesSkipped">Files that couldn't be read at all (missing, locked, binary, too large, denied).</param>
public sealed record SearchSummary(int FilesScanned, int FilesWithHits, int FilesSkipped, int HitCount, bool Truncated);

/// <summary>
/// Searches a set of files for a literal string (ARCHITECTURE M13). The set is whatever the folder pane listed, so
/// it covers the folder's JSON documents as well as its Markdown ones — a search that read only half of what the
/// pane shows would be a quiet lie about where a term does not appear.
/// </summary>
/// <remarks>
/// <para>Files are read through <see cref="IDocumentLoader"/>, the same reader that opens a document, so encodings,
/// BOMs, binary detection, the size cap, locked files and access-denied behave exactly as they do in a tab — there
/// is no second file-reading policy to keep in sync.</para>
/// <para>Results are handed over one file at a time, which is both how the panel groups them and what keeps a
/// long search from posting thousands of times to the UI thread. Files are read one after another, so the order of
/// the batches is the order of <c>files</c>.</para>
/// </remarks>
public static class FileSearch
{
    /// <summary>Longest context line a hit carries; longer lines are cut around the match with "…".</summary>
    public const int MaxContextChars = 240;

    /// <summary>Characters kept before the match when a line has to be cut.</summary>
    private const int ContextLead = 48;

    private const string Ellipsis = "…";

    /// <summary>
    /// Every match of <paramref name="query"/> in <paramref name="text"/>. Pure: no file system, no cancellation,
    /// no allocation beyond the hits themselves. Used for the files on disk and for text already in memory.
    /// </summary>
    public static IReadOnlyList<SearchHit> ScanText(string filePath, string text, SearchQuery query)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrEmpty(text) || !query.IsUsable || query.MaxHitsPerFile <= 0)
        {
            return [];
        }

        var hits = new List<SearchHit>();
        ReadOnlySpan<char> needle = query.Text;
        int lineNumber = 0;
        foreach (ReadOnlySpan<char> line in EnumerateMarkdownLines(text))
        {
            lineNumber++;
            int offset = 0;
            while (offset <= line.Length - needle.Length)
            {
                int found = line[offset..].IndexOf(needle, query.Comparison);
                if (found < 0)
                {
                    break;
                }

                int start = offset + found;
                hits.Add(CreateHit(filePath, lineNumber, line, start, needle.Length));
                if (hits.Count >= query.MaxHitsPerFile)
                {
                    return hits;
                }

                offset = start + Math.Max(needle.Length, 1);
            }
        }

        return hits;
    }

    /// <summary>
    /// The lines of <paramref name="text"/> the way CommonMark counts them: <c>\r\n</c>, <c>\r</c> and <c>\n</c>,
    /// and nothing else.
    /// </summary>
    /// <remarks>
    /// <see cref="MemoryExtensions.EnumerateLines"/> also breaks on form feed, vertical tab, U+0085, U+2028 and
    /// U+2029, which Markdig does not. A hit's line number has to agree with <c>TocEntry.Line</c> — a document
    /// holding one form feed would otherwise report every later hit one line too far and jump to the wrong
    /// heading (M13).
    /// </remarks>
    internal static MarkdownLineEnumerator EnumerateMarkdownLines(ReadOnlySpan<char> text) => new(text);

    /// <summary>The enumerator behind <see cref="EnumerateMarkdownLines"/>; its own <c>GetEnumerator</c> lets it be
    /// used directly in a <c>foreach</c> without allocating.</summary>
    internal ref struct MarkdownLineEnumerator(ReadOnlySpan<char> text)
    {
        private ReadOnlySpan<char> _remaining = text;
        private bool _done;

        public ReadOnlySpan<char> Current { get; private set; } = default;

        public readonly MarkdownLineEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            if (_done)
            {
                return false;
            }

            int index = _remaining.IndexOfAny('\r', '\n');
            if (index < 0)
            {
                Current = _remaining;
                _done = true;
                return true;
            }

            Current = _remaining[..index];
            int terminator = _remaining[index] == '\r' && index + 1 < _remaining.Length && _remaining[index + 1] == '\n'
                ? 2
                : 1;
            _remaining = _remaining[(index + terminator)..];
            if (_remaining.IsEmpty)
            {
                _done = true;   // a final line terminator doesn't start another line
            }

            return true;
        }
    }

    /// <summary>
    /// Searches <paramref name="files"/> in order, calling <paramref name="onFileHits"/> once per file that has
    /// any, on whatever thread the read completed on. Cancelling throws <see cref="OperationCanceledException"/>
    /// after at most one more file is read.
    /// </summary>
    public static async Task<SearchSummary> RunAsync(IReadOnlyList<string> files, SearchQuery query, IDocumentLoader loader,
                                                     Action<IReadOnlyList<SearchHit>> onFileHits,
                                                     CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(loader);
        ArgumentNullException.ThrowIfNull(onFileHits);
        if (!query.IsUsable)
        {
            return new SearchSummary(0, 0, 0, 0, Truncated: false);
        }

        int scanned = 0;
        int withHits = 0;
        int skipped = 0;
        int total = 0;
        bool truncated = false;

        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (total >= query.MaxHits)
            {
                truncated = true;
                break;
            }

            DocumentLoadResult result;
            try
            {
                result = await loader.LoadAsync(file, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                skipped++;   // the loader reports its own failures; anything that still escapes is one skipped file
                continue;
            }

            if (result is not DocumentLoaded loaded)
            {
                skipped++;
                continue;
            }

            scanned++;
            var perFile = query with { MaxHitsPerFile = Math.Min(query.MaxHitsPerFile, query.MaxHits - total) };

            // The caller's own spelling of the path, not the loader's: the tree, the tabs and the opener all use it.
            IReadOnlyList<SearchHit> hits = ScanText(file, loaded.Text, perFile);
            if (hits.Count == 0)
            {
                continue;
            }

            total += hits.Count;
            withHits++;
            onFileHits(hits);
        }

        return new SearchSummary(scanned, withHits, skipped, total, truncated);
    }

    /// <summary>The hit's context line, cut around the match so a minified 2 MB line can't reach the UI.</summary>
    private static SearchHit CreateHit(string filePath, int lineNumber, ReadOnlySpan<char> line, int matchStart, int matchLength)
    {
        if (line.Length <= MaxContextChars)
        {
            // Leading indentation is noise in a results list, and dropping it shifts the match with it.
            int indent = 0;
            while (indent < matchStart && char.IsWhiteSpace(line[indent]))
            {
                indent++;
            }

            // Trailing whitespace goes too — but never any that the match itself is made of, or the highlight
            // would claim fewer characters than were matched (searching for "o  " in "foo   ").
            int trimEnd = line.Length;
            while (trimEnd > matchStart + matchLength && char.IsWhiteSpace(line[trimEnd - 1]))
            {
                trimEnd--;
            }

            string trimmed = line[indent..trimEnd].ToString();
            int trimmedStart = Math.Clamp(matchStart - indent, 0, trimmed.Length);
            return new SearchHit(filePath, lineNumber, trimmed, trimmedStart,
                                 Math.Clamp(matchLength, 0, trimmed.Length - trimmedStart));
        }

        int start = Math.Max(0, matchStart - ContextLead);
        start = AlignLow(line, start);
        int end = Math.Min(line.Length, start + MaxContextChars);
        end = AlignHigh(line, end);
        if (end <= start)
        {
            end = Math.Min(line.Length, start + 1);
        }

        string prefix = start > 0 ? Ellipsis : "";
        string suffix = end < line.Length ? Ellipsis : "";
        string context = prefix + line[start..end].ToString() + suffix;

        // The match may itself be cut short by the window; report only the part the context actually shows.
        int contextStart = Math.Clamp(prefix.Length + Math.Max(0, matchStart - start), 0, context.Length);
        int visible = Math.Clamp(matchStart + matchLength, start, end) - Math.Max(matchStart, start);
        return new SearchHit(filePath, lineNumber, context, contextStart,
                             Math.Clamp(visible, 0, context.Length - contextStart));
    }

    /// <summary>Moves a cut point back off the low half of a surrogate pair.</summary>
    private static int AlignLow(ReadOnlySpan<char> line, int index) =>
        index > 0 && index < line.Length && char.IsLowSurrogate(line[index]) ? index - 1 : index;

    /// <summary>Moves a cut point back off the high half of a surrogate pair.</summary>
    private static int AlignHigh(ReadOnlySpan<char> line, int index) =>
        index > 0 && index < line.Length && char.IsHighSurrogate(line[index - 1]) ? index - 1 : index;
}
