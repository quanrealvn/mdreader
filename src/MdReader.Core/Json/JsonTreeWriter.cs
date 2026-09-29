using System.Globalization;
using System.Text;
using System.Text.Json;
using MdReader.Core.Rendering;

namespace MdReader.Core.Json;

/// <summary>
/// Pass two: the same token walk as <see cref="JsonScanner"/>, writing the tree (JSON-PLAN, "Tree — the default").
/// </summary>
/// <remarks>
/// <para>Objects and arrays become <c>details</c>/<c>summary</c>, which collapse with no JavaScript at all, so the
/// page works before enhancement, print already opens them and the browser's own find runs over the text. Their size
/// is in the summary, so a collapsed branch still says something. Scalars are one row each, typed by class rather
/// than by guesswork: a string is quoted, a number, a boolean and <c>null</c> are not.</para>
/// <para>Three rules the plan calls out and this file keeps: a number is copied out of the source bytes and never
/// parsed, so <c>1.50</c>, <c>1e3</c> and a 19-digit id survive intact; keys are written in the order the document
/// wrote them; and a name that appears twice appears twice, because a reader that silently drops one is lying about
/// the file.</para>
/// </remarks>
internal static class JsonTreeWriter
{
    private const int CancellationCheckInterval = 4096;

    /// <summary>An array at least this long shows the index of each of its items.</summary>
    private const int IndexFrom = 10;

    /// <summary>U+FFFD, for a byte that cannot be what the grammar says it is.</summary>
    private const char Replacement = (char)0xFFFD;

    /// <summary>Writes the tree into <paramref name="output"/> and returns the contents entries it found.</summary>
    /// <exception cref="JsonScanner.TooBigException">The tree outgrew <see cref="JsonRenderOptions.HtmlBudget"/>.</exception>
    public static IReadOnlyList<TocEntry> Write(
        StringBuilder output,
        ReadOnlySpan<byte> utf8,
        JsonScan scan,
        JsonRenderOptions options,
        CancellationToken cancellationToken)
    {
        var state = new State(output, scan.ChildCounts, options);
        var budget = options.HtmlBudget(utf8.Length);
        var tokens = 0;

        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            MaxDepth = options.MaxDepth + 4,
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
        });

        while (reader.Read())
        {
            if (++tokens % CancellationCheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    state.SetKey(reader.GetString() ?? string.Empty, reader.TokenStartIndex);
                    continue;

                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    state.OpenContainer(reader.TokenType == JsonTokenType.StartArray);
                    break;

                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    state.CloseContainer();
                    break;

                case JsonTokenType.String:
                    state.WriteString(reader.GetString() ?? string.Empty);
                    break;

                case JsonTokenType.Number:
                    state.WriteNumber(ref reader);
                    break;

                case JsonTokenType.True:
                    state.WriteLiteral("json-boolean", "true");
                    break;

                case JsonTokenType.False:
                    state.WriteLiteral("json-boolean", "false");
                    break;

                case JsonTokenType.Null:
                    state.WriteLiteral("json-null", "null");
                    break;

                default:
                    continue;
            }

            // One token adds a bounded amount of markup, so checking here overshoots by at most one row.
            if (output.Length > budget)
            {
                throw new JsonScanner.TooBigException(JsonScanner.OutputTooLargeNotice);
            }
        }

        return state.BuildToc(utf8);
    }

    /// <summary>Everything the walk carries between tokens.</summary>
    private sealed class State(StringBuilder output, List<int> childCounts, JsonRenderOptions options)
    {
        private readonly List<TocEntry> _toc = [];
        private readonly List<long> _tocOffsets = [];
        private Frame[] _frames = new Frame[32];
        private int _depth;
        private int _containerOrdinal;

        private string? _key;
        private long _keyOffset;

        /// <summary>The id given to the row about to be written, or null. Only top-level keys get one.</summary>
        private string? _rowId;

        public void SetKey(string key, long offset)
        {
            _key = key;
            _keyOffset = offset;
        }

        public void OpenContainer(bool isArray)
        {
            var count = childCounts[_containerOrdinal++];
            var key = TakeKey();
            var index = TakeIndex();
            OpenRow();

            if (count == 0)
            {
                // An empty object or array is shown, never silently omitted. It still gets a frame, so that its end
                // token knows there is no <details> of its own to close.
                WritePrefix(key, index);
                output.Append("<span class=\"json-empty\">").Append(isArray ? "[]" : "{}").Append("</span>");
                CloseRow();
                Push(isArray, showIndex: false, isEmpty: true);
                return;
            }

            // open="" rather than a bare open: that is how the sanitizer serializes the attribute, and this HTML is
            // required to come back from it unchanged (see JsonText).
            output.Append("<details class=\"json-node\"");
            if (_depth < options.OpenDepth)
            {
                output.Append(" open=\"\"");
            }

            output.Append("><summary class=\"json-summary\">");
            WritePrefix(key, index);
            AppendSize(isArray, count);
            output.Append("</summary><ul class=\"").Append(isArray ? "json-array" : "json-object").Append("\">\n");

            Push(isArray, count >= IndexFrom, isEmpty: false);
        }

        public void CloseContainer()
        {
            _depth--;
            if (_frames[_depth].IsEmpty)
            {
                return;
            }

            output.Append("</ul></details>");
            CloseRow();
        }

        public void WriteString(string value)
        {
            BeginScalar();
            output.Append("<span class=\"json-string\">\"");
            var elided = JsonText.AppendStringBody(output, value, options.MaxStringLength);
            if (elided > 0)
            {
                output.Append('…');
            }

            output.Append("\"</span>");
            AppendElided(elided);
            CloseRow();
        }

        public void WriteLiteral(string cssClass, string text)
        {
            BeginScalar();
            output.Append("<span class=\"").Append(cssClass).Append("\">").Append(text).Append("</span>");
            CloseRow();
        }

        /// <summary>
        /// The number exactly as the document wrote it. The bytes of the token are copied out; nothing is parsed, so
        /// no precision, no trailing zero and no exponent notation is lost. A number long enough to be an attack
        /// rather than a number is elided like a long string, with the elision shown.
        /// </summary>
        public void WriteNumber(ref Utf8JsonReader reader)
        {
            BeginScalar();
            output.Append("<span class=\"json-number\">");

            var elided = 0;
            if (reader.HasValueSequence)
            {
                // Only possible over a multi-segment input, which this renderer never builds; handled so that it
                // could never silently write nothing.
                var remaining = options.MaxStringLength;
                foreach (var segment in reader.ValueSequence)
                {
                    var take = Math.Min(remaining, segment.Length);
                    AppendAscii(segment.Span[..take]);
                    remaining -= take;
                    elided += segment.Length - take;
                }
            }
            else
            {
                var digits = reader.ValueSpan;
                var shown = Math.Min(digits.Length, options.MaxStringLength);
                AppendAscii(digits[..shown]);
                elided = digits.Length - shown;
            }

            if (elided > 0)
            {
                output.Append('…');
            }

            output.Append("</span>");
            AppendElided(elided);
            CloseRow();
        }

        /// <summary>The contents entries, with the source line of each key filled in.</summary>
        public IReadOnlyList<TocEntry> BuildToc(ReadOnlySpan<byte> utf8)
        {
            var line = 1;
            var scanned = 0;
            for (var i = 0; i < _toc.Count; i++)
            {
                var target = (int)Math.Clamp(_tocOffsets[i], 0, utf8.Length);
                for (; scanned < target; scanned++)
                {
                    if (utf8[scanned] == (byte)'\n')
                    {
                        line++;
                    }
                }

                _toc[i] = _toc[i] with { Line = line };
            }

            return _toc;
        }

        private void BeginScalar()
        {
            var key = TakeKey();
            var index = TakeIndex();
            OpenRow();
            WritePrefix(key, index);
        }

        /// <summary>The key this value is stored under, or null when it is an array item or the whole document.</summary>
        private string? TakeKey()
        {
            if (_depth == 0 || _frames[_depth - 1].IsArray)
            {
                return null;
            }

            var key = _key ?? string.Empty;
            _key = null;

            // Only a member of a top-level object earns a contents entry and the id it points at. A document whose
            // root is an array would contribute a list of indices, which is not a contents panel (JSON-PLAN's open
            // question); it gets none.
            _rowId = _depth == 1 && _toc.Count < options.MaxTocEntries
                ? AddTocEntry(key)
                : null;

            return key;
        }

        /// <summary>The item's index, or -1 when it is not an item of an array long enough to number.</summary>
        private int TakeIndex()
        {
            if (_depth == 0)
            {
                return -1;
            }

            ref var frame = ref _frames[_depth - 1];
            if (!frame.IsArray)
            {
                return -1;
            }

            var index = frame.Index++;
            return frame.ShowIndex ? index : -1;
        }

        private string AddTocEntry(string key)
        {
            var id = "json-" + _toc.Count.ToString(CultureInfo.InvariantCulture);
            _toc.Add(new TocEntry(1, id, TocText(key)));
            _tocOffsets.Add(_keyOffset);
            return id;
        }

        private void OpenRow()
        {
            if (_depth == 0)
            {
                return;
            }

            output.Append("<li class=\"json-item\"");
            if (_rowId is not null)
            {
                output.Append(" id=\"").Append(_rowId).Append('"');
                _rowId = null;
            }

            output.Append('>');
        }

        private void CloseRow()
        {
            if (_depth > 0)
            {
                output.Append("</li>\n");
            }
        }

        private void WritePrefix(string? key, int index)
        {
            if (key is not null)
            {
                output.Append("<span class=\"json-key\">");
                if (JsonText.AppendStringBody(output, key, options.MaxStringLength) > 0)
                {
                    output.Append('…');
                }

                output.Append("</span>: ");
            }
            else if (index >= 0)
            {
                output.Append("<span class=\"json-index\">")
                    .Append(index.ToString(CultureInfo.InvariantCulture))
                    .Append("</span>: ");
            }
        }

        private void AppendSize(bool isArray, int count)
        {
            var number = count.ToString("N0", CultureInfo.InvariantCulture);
            output.Append("<span class=\"json-size\">")
                .Append(isArray ? '[' : '{')
                .Append(number)
                .Append(isArray ? (count == 1 ? " item]" : " items]") : (count == 1 ? " key}" : " keys}"))
                .Append("</span>");
        }

        private void AppendElided(int elided)
        {
            if (elided > 0)
            {
                output.Append("<span class=\"json-elided\"> +")
                    .Append(elided.ToString("N0", CultureInfo.InvariantCulture))
                    .Append(" more characters</span>");
            }
        }

        /// <summary>
        /// A JSON number token is ASCII by its grammar. A byte that is not is written as the replacement character
        /// rather than as whatever Latin-1 would make of it, and the three characters that could start markup are
        /// escaped, so even a reader that handed us something impossible could not put an element on the page.
        /// </summary>
        private void AppendAscii(ReadOnlySpan<byte> bytes)
        {
            foreach (var b in bytes)
            {
                switch (b)
                {
                    case (byte)'&':
                        output.Append("&amp;");
                        break;
                    case (byte)'<':
                        output.Append("&lt;");
                        break;
                    case (byte)'>':
                        output.Append("&gt;");
                        break;
                    default:
                        output.Append(b >= 0x80 ? Replacement : (char)b);
                        break;
                }
            }
        }

        private void Push(bool isArray, bool showIndex, bool isEmpty)
        {
            if (_depth == _frames.Length)
            {
                Array.Resize(ref _frames, _frames.Length * 2);
            }

            _frames[_depth++] = new Frame { IsArray = isArray, ShowIndex = showIndex, IsEmpty = isEmpty };
        }

        /// <summary>The key as plain text for the contents panel: capped like a heading, and one line.</summary>
        private static string TocText(string key)
        {
            var text = key.Length > TocExtractor.MaxTextLength
                ? key[..TocExtractor.MaxTextLength].TrimEnd() + "…"
                : key;

            char[]? buffer = null;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] >= ' ')
                {
                    continue;
                }

                buffer ??= text.ToCharArray();
                buffer[i] = ' ';
            }

            return buffer is null ? text : new string(buffer);
        }

        private struct Frame
        {
            public bool IsArray;
            public bool ShowIndex;

            /// <summary>An object or array with no children: written whole at its start token, nothing to close.</summary>
            public bool IsEmpty;
            public int Index;
        }
    }
}
