using System.Text;
using Markdig.Extensions.Mathematics;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MdReader.Core.Rendering;

/// <summary>
/// Table of contents and heading plain text (ARCHITECTURE §4.1). Every <see cref="HeadingBlock"/> in document order;
/// <c>Id</c> goes through <see cref="ContentIdPolicy"/> exactly like the sanitizer does for the id attribute.
/// </summary>
internal static class TocExtractor
{
    /// <summary>All headings of <paramref name="document"/> in document order (headings whose id is dropped are skipped).</summary>
    public static IReadOnlyList<TocEntry> Extract(MarkdownDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var entries = new List<TocEntry>();
        foreach (var heading in document.Descendants<HeadingBlock>())
        {
            if (CreateEntry(heading) is { } entry)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    /// <summary>The TOC entry of one heading, or null if its id is dropped by <see cref="ContentIdPolicy"/>.</summary>
    public static TocEntry? CreateEntry(HeadingBlock heading)
    {
        ArgumentNullException.ThrowIfNull(heading);
        return CreateEntry(heading, GetPlainText(heading));
    }

    /// <summary>As <see cref="CreateEntry(HeadingBlock)"/>, with the heading's plain text already computed.</summary>
    public static TocEntry? CreateEntry(HeadingBlock heading, string text)
    {
        // Same function the sanitizer applies to the id attribute, so TocEntry.Id == the id in the HTML.
        var id = ContentIdPolicy.ToContentId(heading.TryGetAttributes()?.Id);
        return id is null ? null : new TocEntry(heading.Level, id, text);
    }

    /// <summary>
    /// Plain text of a heading's inline tree: literals, code spans, emoji, math source, entities and link/image text.
    /// Raw HTML tags are skipped. Whitespace runs collapse to one space; the result is trimmed and capped at
    /// <see cref="MaxTextLength"/> characters plus "…" (the TOC and the window title travel in one message; a 3 MB
    /// heading stalled the UI for 146 ms).
    /// </summary>
    public static string GetPlainText(LeafBlock block)
    {
        ArgumentNullException.ThrowIfNull(block);
        if (block.Inline is null)
        {
            return string.Empty;
        }

        var text = new PlainTextBuilder(MaxTextLength);
        foreach (var inline in EnumerateInlines(block.Inline))
        {
            switch (inline)
            {
                case LiteralInline literal:         // includes EmojiInline (Content = the emoji)
                    text.Append(literal.Content.AsSpan());
                    break;
                case CodeInline code:
                    text.Append(code.Content);
                    break;
                case MathInline math:
                    text.Append(math.Content.AsSpan());
                    break;
                case HtmlEntityInline entity:
                    text.Append(entity.Transcoded.AsSpan());
                    break;
                case AutolinkInline autolink:
                    text.Append(autolink.Url);
                    break;
                case LineBreakInline:
                    text.Append(" ");
                    break;
                default:
                    break;      // raw HTML tags are skipped (their text siblings are kept); containers are descended
            }

            if (text.IsFull)
            {
                break;
            }
        }

        return text.ToString();
    }

    /// <summary>Longest heading text (TOC entry text, document title) before it is cut off with "…".</summary>
    public const int MaxTextLength = 1_000;

    /// <summary>Cuts <paramref name="text"/> to <paramref name="maxLength"/> characters (never inside a surrogate pair).</summary>
    internal static string Truncate(string text, int maxLength)
    {
        if (text.Length <= maxLength)
        {
            return text;
        }

        var length = char.IsHighSurrogate(text[maxLength - 1]) ? maxLength - 1 : maxLength;
        return text[..length];
    }

    /// <summary>
    /// Every inline below <paramref name="root"/> in document (pre-)order: containers (emphasis, links, images → alt text,
    /// leftover delimiters) are yielded and then descended. Iterative, without a stack: right after parsing, Markdig does
    /// not yet enforce its nesting limit on emphasis, so an inline tree can be ~100 000 levels deep.
    /// </summary>
    internal static IEnumerable<Inline> EnumerateInlines(ContainerInline root)
    {
        var inline = root.FirstChild;
        while (inline is not null)
        {
            yield return inline;

            if (inline is ContainerInline { FirstChild: { } firstChild })
            {
                inline = firstChild;
                continue;
            }

            // Next sibling, or the next sibling of the nearest ancestor that has one (stopping at the root).
            while (inline is not null && inline.NextSibling is null)
            {
                var parent = inline.Parent;
                inline = ReferenceEquals(parent, root) ? null : parent;
            }

            inline = inline?.NextSibling;
        }
    }

    /// <summary>Collapses whitespace runs to one space, trims, and stops collecting once the cap is exceeded.</summary>
    private sealed class PlainTextBuilder(int maxLength)
    {
        private readonly StringBuilder _result = new();
        private bool _pendingSpace;

        /// <summary>True once more than the cap has been collected (further input is ignored).</summary>
        public bool IsFull => _result.Length > maxLength;

        public void Append(ReadOnlySpan<char> value)
        {
            foreach (var c in value)
            {
                if (IsFull)
                {
                    return;
                }

                if (char.IsWhiteSpace(c))
                {
                    _pendingSpace = _result.Length > 0;
                    continue;
                }

                if (_pendingSpace)
                {
                    _result.Append(' ');
                    _pendingSpace = false;
                }

                _result.Append(c);
            }
        }

        public override string ToString()
        {
            var text = _result.ToString();
            return text.Length > maxLength ? Truncate(text, maxLength).TrimEnd() + "…" : text;
        }
    }
}
