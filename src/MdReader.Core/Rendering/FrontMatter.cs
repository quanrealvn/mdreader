using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace MdReader.Core.Rendering;

/// <summary>One front-matter key with the value(s) that were read for it, in document order.</summary>
/// <param name="Key">The key path; nested maps are flattened with dots (<c>author.name</c>).</param>
/// <param name="Values">One entry for a scalar, several for a sequence. Never empty (a valueless key has <c>[""]</c>).</param>
/// <param name="IsSequence">True when the source wrote a sequence, so the renderer can lay the values out as a list.</param>
internal sealed record FrontMatterEntry(string Key, IReadOnlyList<string> Values, bool IsSequence);

/// <summary>What <see cref="FrontMatterParser"/> could make of a document's YAML front matter (ARCHITECTURE §4.1).</summary>
/// <param name="Entries">The keys that were understood, in source order.</param>
/// <param name="Raw">
/// The front matter verbatim, set only when at least one line couldn't be read (or nothing could). The card shows it so
/// a document with malformed YAML still displays everything its author wrote.
/// </param>
/// <param name="Truncated">True when a limit stopped the parse, so the card can say the list is incomplete.</param>
internal sealed record FrontMatterData(IReadOnlyList<FrontMatterEntry> Entries, string? Raw, bool Truncated)
{
    public static FrontMatterData Empty { get; } = new([], null, false);

    public bool IsEmpty => Entries.Count == 0 && string.IsNullOrEmpty(Raw);
}

/// <summary>
/// A deliberately small YAML reader for front matter: <c>key: value</c>, quoted scalars, <c>- item</c> sequences,
/// <c>[a, b]</c> flow sequences, <c>|</c>/<c>&gt;</c> block scalars and nested maps flattened to <c>parent.child</c>.
/// </summary>
/// <remarks>
/// It is not a YAML implementation and doesn't try to be: front matter is untrusted document content, so the reader is
/// line-based, allocation-bounded, never recursive and never throws. Anything it can't read is handed back as
/// <see cref="FrontMatterData.Raw"/> rather than guessed at, and every limit below caps the work at a constant the
/// document can't influence.
/// </remarks>
internal static class FrontMatterParser
{
    /// <summary>Lines beyond this are not looked at (the card would be unreadable long before).</summary>
    internal const int MaxLines = 1_000;

    /// <summary>Keys beyond this are dropped and the result is marked truncated.</summary>
    internal const int MaxEntries = 200;

    /// <summary>Sequence items (or block-scalar lines) kept per key.</summary>
    internal const int MaxValuesPerEntry = 100;

    /// <summary>Longest key path kept; longer keys are truncated.</summary>
    internal const int MaxKeyLength = 120;

    /// <summary>Longest value kept; longer values are truncated with an ellipsis.</summary>
    internal const int MaxValueLength = 400;

    /// <summary>Nesting levels kept; deeper maps flatten onto their deepest kept ancestor.</summary>
    internal const int MaxDepth = 4;

    /// <summary>Characters of verbatim front matter shown when it couldn't be read.</summary>
    internal const int MaxRawLength = 4_000;

    private const string Ellipsis = "…";

    /// <summary>Reads <paramref name="lines"/> — the front matter body, without its <c>---</c> fences.</summary>
    public static FrontMatterData Parse(IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var state = new ParseState();
        var lineCount = Math.Min(lines.Count, MaxLines);
        if (lines.Count > MaxLines)
        {
            state.Truncated = true;
        }

        for (var i = 0; i < lineCount; i++)
        {
            if (!state.ReadLine(lines[i]))
            {
                break;
            }
        }

        state.CloseBlockScalar();
        return state.Build(lines);
    }

    /// <summary>The parse of one front matter: a flat list of entries plus the bookkeeping that fills it.</summary>
    private sealed class ParseState
    {
        private readonly List<Entry> _entries = [];
        private readonly Dictionary<string, int> _byKey = new(StringComparer.Ordinal);

        /// <summary>The maps currently open: the indent each was written at, its key, and the row it created.</summary>
        private readonly List<(int Indent, string Key, int EntryIndex)> _parents = [];

        /// <summary>The entry a following <c>- item</c> line attaches to, and the indent of its key.</summary>
        private int _pendingSequenceEntry = -1;
        private int _pendingSequenceIndent;

        private BlockScalar? _blockScalar;
        private bool _sawUnreadableLine;

        public bool Truncated { get; set; }

        /// <summary>Reads one line; false ends the parse (a second YAML document started).</summary>
        public bool ReadLine(string line)
        {
            var indent = IndentOf(line);
            var trimmed = line.AsSpan(indent).TrimEnd();

            if (_blockScalar is { } block)
            {
                // Everything indented past the key belongs to the scalar, blank lines included.
                if (trimmed.IsEmpty || indent > block.KeyIndent)
                {
                    block.Add(line, indent, trimmed.IsEmpty);
                    return true;
                }

                CloseBlockScalar();
            }

            if (trimmed.IsEmpty || trimmed[0] == '#')
            {
                return true;
            }

            if (trimmed is "---" or "...")
            {
                return false;   // a second document: nothing after this belongs to the front matter
            }

            if (trimmed[0] == '-' && (trimmed.Length == 1 || trimmed[1] == ' '))
            {
                AddSequenceItem(indent, trimmed.Length == 1 ? default : trimmed[2..]);
                return true;
            }

            if (!TrySplitKey(trimmed, out var key, out var value))
            {
                _sawUnreadableLine = true;
                return true;
            }

            AddKey(indent, key, value);
            return true;
        }

        public void CloseBlockScalar()
        {
            if (_blockScalar is not { } block)
            {
                return;
            }

            _blockScalar = null;
            var entry = _entries[block.EntryIndex];
            entry.Values.Clear();
            entry.Values.Add(Clamp(block.Text(), MaxValueLength));
        }

        public FrontMatterData Build(IReadOnlyList<string> lines)
        {
            var entries = new List<FrontMatterEntry>(_entries.Count);
            foreach (var entry in _entries)
            {
                // A key that turned out to be a map header ("author:" followed by indented keys) is not a row of its
                // own — its children carry the whole path.
                if (entry.HasChildren && entry.Values is [""])
                {
                    continue;
                }

                entries.Add(new FrontMatterEntry(entry.Key, entry.Values.Count == 0 ? [string.Empty] : entry.Values,
                    entry.IsSequence));
            }

            var raw = _sawUnreadableLine || entries.Count == 0 ? RawText(lines) : null;
            return entries.Count == 0 && string.IsNullOrEmpty(raw)
                ? FrontMatterData.Empty
                : new FrontMatterData(entries, raw, Truncated);
        }

        private void AddKey(int indent, string key, ReadOnlySpan<char> value)
        {
            // A key at or left of an open map's own indent closes it.
            while (_parents.Count > 0 && indent <= _parents[^1].Indent)
            {
                _parents.RemoveAt(_parents.Count - 1);
            }

            if (_parents.Count > 0)
            {
                _entries[_parents[^1].EntryIndex].HasChildren = true;
            }

            var path = BuildPath(key);

            if (value.IsEmpty)
            {
                // Either a map header (indented keys follow) or a key whose sequence items follow. Record it as a row
                // right away so a key with neither still shows up, and let the following lines fill it in.
                var index = AddEntry(path, string.Empty, isSequence: false);
                if (index >= 0 && _parents.Count < MaxDepth)
                {
                    // Clamped going in, not on the way out: an unclamped key here is rebuilt into
                    // every descendant's path and then thrown away, which turns one long key into
                    // 2 × MaxLines copies of it — work the caps below are supposed to rule out.
                    _parents.Add((indent, Clamp(key, MaxKeyLength), index));
                }

                _pendingSequenceEntry = index;
                _pendingSequenceIndent = indent;
                return;
            }

            _pendingSequenceEntry = -1;

            if (value[0] is '|' or '>' && IsBlockScalarHeader(value))
            {
                var index = AddEntry(path, string.Empty, isSequence: false);
                if (index >= 0)
                {
                    _blockScalar = new BlockScalar(index, indent, folded: value[0] == '>');
                }

                return;
            }

            if (value[0] == '[' && value[^1] == ']')
            {
                AddFlowSequence(path, value[1..^1]);
                return;
            }

            AddEntry(path, ScalarValue(value), isSequence: false);
        }

        private void AddSequenceItem(int indent, ReadOnlySpan<char> item)
        {
            // Sequence items may be written at the key's own indent or deeper; anything shallower isn't ours.
            if (_pendingSequenceEntry < 0 || indent < _pendingSequenceIndent)
            {
                _sawUnreadableLine = true;
                return;
            }

            var entry = _entries[_pendingSequenceEntry];
            entry.IsSequence = true;
            if (entry.Values is [""])
            {
                entry.Values.Clear();
            }

            if (entry.Values.Count >= MaxValuesPerEntry)
            {
                Truncated = true;
                return;
            }

            entry.Values.Add(ScalarValue(item));
        }

        private void AddFlowSequence(string path, ReadOnlySpan<char> inner)
        {
            var index = AddEntry(path, string.Empty, isSequence: true);
            if (index < 0)
            {
                return;
            }

            var values = _entries[index].Values;
            values.Clear();
            foreach (var item in SplitFlow(inner))
            {
                if (values.Count >= MaxValuesPerEntry)
                {
                    Truncated = true;
                    break;
                }

                values.Add(item);
            }
        }

        /// <summary>Adds a row (or overwrites the one with the same key) and returns its index, or -1 when full.</summary>
        private int AddEntry(string key, string value, bool isSequence)
        {
            if (_byKey.TryGetValue(key, out var existing))
            {
                // A repeated key: the last one wins, like every YAML loader. The row is reused so the order is stable.
                var entry = _entries[existing];
                entry.Values.Clear();
                entry.Values.Add(value);
                entry.IsSequence = isSequence;
                return existing;
            }

            if (_entries.Count >= MaxEntries)
            {
                Truncated = true;
                return -1;
            }

            _entries.Add(new Entry(key, value, isSequence));
            _byKey[key] = _entries.Count - 1;
            return _entries.Count - 1;
        }

        private string BuildPath(string key)
        {
            if (_parents.Count == 0)
            {
                return Clamp(key, MaxKeyLength);
            }

            var builder = new StringBuilder();
            foreach (var (_, parent, _) in _parents)
            {
                builder.Append(parent).Append('.');
            }

            return Clamp(builder.Append(key).ToString(), MaxKeyLength);
        }

        private string RawText(IReadOnlyList<string> lines)
        {
            var builder = new StringBuilder();
            for (var i = 0; i < lines.Count && builder.Length <= MaxRawLength; i++)
            {
                if (i > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(lines[i]);
            }

            if (builder.Length > MaxRawLength)
            {
                // Same rule as Clamp: never cut between the halves of a surrogate pair.
                builder.Length = char.IsHighSurrogate(builder[MaxRawLength - 1]) ? MaxRawLength - 1 : MaxRawLength;
                builder.Append(Ellipsis);
                Truncated = true;
            }

            // Front matter that is only blank lines has nothing to show verbatim either.
            var raw = builder.ToString().Trim('\n');
            return string.IsNullOrWhiteSpace(raw) ? string.Empty : raw;
        }

        /// <summary>A mutable row while parsing; <see cref="Build"/> freezes these into <see cref="FrontMatterEntry"/>.</summary>
        private sealed class Entry(string key, string value, bool isSequence)
        {
            public string Key { get; } = key;

            public List<string> Values { get; } = [value];

            public bool IsSequence { get; set; } = isSequence;

            /// <summary>Set when an indented key was read under this one, which makes this row a map header.</summary>
            public bool HasChildren { get; set; }
        }

        /// <summary>An open <c>|</c>/<c>&gt;</c> block scalar and the lines gathered for it so far.</summary>
        private sealed class BlockScalar(int entryIndex, int keyIndent, bool folded)
        {
            private readonly List<string> _lines = [];

            /// <summary>The indent of the scalar's first non-blank line; it is stripped from every line.</summary>
            private int _textIndent = int.MaxValue;

            public int EntryIndex { get; } = entryIndex;

            public int KeyIndent { get; } = keyIndent;

            public void Add(string line, int indent, bool blank)
            {
                if (_lines.Count >= MaxValuesPerEntry)
                {
                    return;
                }

                if (blank)
                {
                    _lines.Add(string.Empty);
                    return;
                }

                if (_textIndent == int.MaxValue)
                {
                    _textIndent = indent;
                }

                _lines.Add(line[Math.Min(indent, _textIndent)..].TrimEnd());
            }

            public string Text()
            {
                // Dropping the trailing blank lines is the "clip" chomping YAML applies by default.
                var end = _lines.Count;
                while (end > 0 && _lines[end - 1].Length == 0)
                {
                    end--;
                }

                return string.Join(folded ? " " : "\n", _lines.Take(end));
            }
        }
    }

    /// <summary>Leading whitespace, counting a tab as one column (YAML forbids tabs there; this only has to be stable).</summary>
    private static int IndentOf(string line)
    {
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
        {
            i++;
        }

        return i;
    }

    /// <summary>
    /// Splits <c>key: value</c> at the first colon that is followed by a space or the end of the line and isn't inside
    /// quotes — so <c>title: "a: b"</c> and <c>url: https://x</c> both split where a reader would expect.
    /// </summary>
    private static bool TrySplitKey(ReadOnlySpan<char> line, [NotNullWhen(true)] out string? key, out ReadOnlySpan<char> value)
    {
        key = null;
        value = default;

        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (c is '"' or '\'')
            {
                quote = c;
                continue;
            }

            if (c != ':' || (i + 1 < line.Length && line[i + 1] != ' '))
            {
                continue;
            }

            var name = Unquote(line[..i].Trim());
            if (name.Length == 0)
            {
                return false;
            }

            key = name;
            value = i + 1 < line.Length ? StripComment(line[(i + 1)..].Trim()).Trim() : default;
            return true;
        }

        return false;
    }

    /// <summary>True for <c>|</c>, <c>&gt;</c> and their chomping/indent indicators (<c>|-</c>, <c>&gt;+</c>, <c>|2</c>).</summary>
    private static bool IsBlockScalarHeader(ReadOnlySpan<char> value)
    {
        foreach (var c in value[1..])
        {
            if (c is not ('-' or '+') && !char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>An unquoted scalar ends at " #"; a quoted one keeps everything inside the quotes.</summary>
    private static ReadOnlySpan<char> StripComment(ReadOnlySpan<char> value)
    {
        if (value.Length == 0 || value[0] is '"' or '\'')
        {
            return value;
        }

        var hash = value.IndexOf(" #", StringComparison.Ordinal);
        return hash < 0 ? value : value[..hash].TrimEnd();
    }

    private static string ScalarValue(ReadOnlySpan<char> raw) => Clamp(Unquote(StripComment(raw.Trim()).Trim()), MaxValueLength);

    /// <summary>Removes matching quotes and undoes the escapes each quoting style defines.</summary>
    private static string Unquote(ReadOnlySpan<char> value)
    {
        if (value.Length < 2 || value[0] != value[^1])
        {
            return value.ToString();
        }

        if (value[0] == '\'')
        {
            return value[1..^1].ToString().Replace("''", "'", StringComparison.Ordinal);
        }

        if (value[0] != '"')
        {
            return value.ToString();
        }

        var inner = value[1..^1];
        if (!inner.Contains('\\'))
        {
            return inner.ToString();
        }

        var builder = new StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] != '\\' || i + 1 >= inner.Length)
            {
                builder.Append(inner[i]);
                continue;
            }

            i++;
            builder.Append(inner[i] switch
            {
                'n' => '\n',
                't' => '\t',
                'r' => '\r',
                '0' => '\0',
                _ => inner[i],
            });
        }

        return builder.ToString();
    }

    /// <summary>Splits a flow sequence's body on commas that aren't inside quotes or a nested bracket.</summary>
    private static List<string> SplitFlow(ReadOnlySpan<char> inner)
    {
        var items = new List<string>();
        var quote = '\0';
        var depth = 0;
        var start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    break;
                case '[' or '{':
                    depth++;
                    break;
                case ']' or '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    AddItem(items, inner[start..i]);
                    start = i + 1;
                    break;
            }
        }

        AddItem(items, inner[start..]);
        return items;

        static void AddItem(List<string> items, ReadOnlySpan<char> raw)
        {
            var item = ScalarValue(raw);
            if (item.Length > 0)
            {
                items.Add(item);
            }
        }
    }

    /// <summary>Cuts <paramref name="value"/> to <paramref name="max"/> chars plus an ellipsis, never mid-character.</summary>
    private static string Clamp(string value, int max)
    {
        if (value.Length <= max)
        {
            return value;
        }

        // A cut between the halves of a surrogate pair — an emoji straddling the limit — would put
        // a lone U+D83D into the HTML and from there into the protocol JSON.
        var end = max > 0 && char.IsHighSurrogate(value[max - 1]) ? max - 1 : max;
        return string.Concat(value.AsSpan(0, end), Ellipsis);
    }
}
