using Markdig;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Parsers.Inlines;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MdReader.Core.Rendering;

/// <summary>
/// Wiki links: <c>[[Another note]]</c>, the way note-taking tools write a link to a file beside this one.
/// </summary>
/// <remarks>
/// <para>The parser turns the inline into an ordinary <see cref="LinkInline"/> and then gets out of the way. That is
/// the whole design: everything downstream — the sanitizer's allow-list, <c>LinkClassifier</c>, the folder tree, the
/// web version's click handler — already knows what to do with a relative link, and none of it needs to learn a new
/// shape. A wiki link is a spelling, not a feature.</para>
/// <para>What it accepts:</para>
/// <code>
/// [[Guide]]                  -> Guide.md                 "Guide"
/// [[Guide|the guide]]        -> Guide.md                 "the guide"
/// [[Guide#Getting started]]  -> Guide.md#getting-started "Guide &gt; Getting started"
/// [[#Getting started]]       -> #getting-started         "Getting started"
/// [[notes/Guide]]            -> notes/Guide.md           "notes/Guide"
/// [[diagram.png]]            -> diagram.png              "diagram.png"
/// </code>
/// <para><c>.md</c> is appended only when the last segment has no extension at all, which is what those tools do.
/// The fragment is slugged by the same rule as a heading id, so <c>#Getting started</c> finds
/// <c>&lt;h2 id="getting-started"&gt;</c>.</para>
/// <para>A target that would leave the document's folder — absolute, rooted, a drive letter, a scheme, or any
/// <c>..</c> segment — is not turned into a link at all; the text stays as the author typed it. The classifier would
/// refuse to open such a link anyway, and text that was never a link is easier to understand than a link that does
/// nothing when clicked.</para>
/// </remarks>
internal sealed class WikiLinkExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        if (pipeline.InlineParsers.Contains<WikiLinkParser>())
        {
            return;
        }

        // Before the ordinary link parser: both open on '[', and "[[" has to be read as one thing rather than as a
        // link whose text starts with a bracket.
        pipeline.InlineParsers.InsertBefore<LinkInlineParser>(new WikiLinkParser());
    }

    public void Setup(MarkdownPipeline pipeline, Markdig.Renderers.IMarkdownRenderer renderer)
    {
    }
}

/// <summary>Reads <c>[[...]]</c> and emits the link it stands for. See <see cref="WikiLinkExtension"/>.</summary>
internal sealed class WikiLinkParser : InlineParser
{
    /// <summary>The most characters allowed between the brackets, matching the cap on heading text.</summary>
    internal const int MaxInnerLength = 1000;

    /// <summary>What separates the target from the heading inside it, and the target from its label.</summary>
    private const char FragmentSeparator = '#';
    private const char LabelSeparator = '|';

    /// <summary>What a label looks like when the author gave none and the target names a heading.</summary>
    private const string HeadingJoiner = " > ";

    private const string MarkdownExtension = ".md";

    public WikiLinkParser()
    {
        OpeningCharacters = ['['];
    }

    public override bool Match(InlineProcessor processor, ref StringSlice slice)
    {
        ArgumentNullException.ThrowIfNull(processor);

        if (slice.PeekChar(1) != '[')
        {
            return false;
        }

        // Scan without touching `slice`: anything short of a complete, usable wiki link has to leave the text exactly
        // as it was, so the ordinary link parser gets its turn on it.
        var text = slice.Text;
        var innerStart = slice.Start + 2;
        var innerEnd = -1;

        for (var i = innerStart; i <= slice.End; i++)
        {
            var current = text[i];
            if (current is '\n' or '[')
            {
                return false;
            }

            if (current == ']')
            {
                if (i >= slice.End || text[i + 1] != ']')
                {
                    return false;
                }

                innerEnd = i;
                break;
            }

            if (i - innerStart >= MaxInnerLength)
            {
                return false;
            }
        }

        if (innerEnd < 0)
        {
            return false;
        }

        if (!TryRead(text.AsSpan(innerStart, innerEnd - innerStart), out var url, out var label))
        {
            return false;
        }

        var startPosition = processor.GetSourcePosition(slice.Start, out var line, out var column);
        var link = new LinkInline(url, string.Empty)
        {
            IsClosed = true,
            Line = line,
            Column = column,
        };
        link.AppendChild(new LiteralInline(label));

        slice.Start = innerEnd + 2;
        link.Span = new SourceSpan(startPosition, processor.GetSourcePosition(slice.Start - 1));
        processor.Inline = link;
        return true;
    }

    /// <summary>Splits what is between the brackets into the link's address and the words shown for it.</summary>
    private static bool TryRead(ReadOnlySpan<char> inner, out string url, out string label)
    {
        url = string.Empty;
        label = string.Empty;

        var pipe = inner.IndexOf(LabelSeparator);
        var target = (pipe >= 0 ? inner[..pipe] : inner).Trim();
        var given = pipe >= 0 ? inner[(pipe + 1)..].Trim() : ReadOnlySpan<char>.Empty;

        var hash = target.IndexOf(FragmentSeparator);
        var path = (hash >= 0 ? target[..hash] : target).Trim();
        var heading = hash >= 0 ? target[(hash + 1)..].Trim() : ReadOnlySpan<char>.Empty;

        // "[[]]", "[[ | something ]]", "[[#]]" — nothing to point at.
        if (path.IsEmpty && heading.IsEmpty)
        {
            return false;
        }

        if (!path.IsEmpty && LeavesTheFolder(path))
        {
            return false;
        }

        var fragment = heading.IsEmpty
            ? string.Empty
            : FragmentSeparator + LinkHelper.UrilizeAsGfm(heading);

        url = path.IsEmpty ? fragment : Encode(WithMarkdownExtension(path)) + fragment;

        label = !given.IsEmpty ? given.ToString()
            : path.IsEmpty ? heading.ToString()
            : heading.IsEmpty ? path.ToString()
            : string.Concat(path, HeadingJoiner, heading);

        return label.Length > 0;
    }

    /// <summary>
    /// True for a target this must not turn into a link: one that names somewhere other than a file beside this one.
    /// </summary>
    private static bool LeavesTheFolder(ReadOnlySpan<char> path)
    {
        // A colon is a scheme ("http:", "javascript:") or a drive ("C:"); a leading separator is the root of
        // something. Neither belongs in a link to the note next door.
        if (path.Contains(':') || path[0] is '/' or '\\')
        {
            return true;
        }

        foreach (var character in path)
        {
            if (char.IsControl(character))
            {
                return true;
            }
        }

        // ".." anywhere, as a whole segment. "..." and "a..b" are ordinary names.
        var remaining = path;
        while (!remaining.IsEmpty)
        {
            var separator = remaining.IndexOfAny('/', '\\');
            var segment = separator >= 0 ? remaining[..separator] : remaining;
            if (segment.SequenceEqual(".."))
            {
                return true;
            }

            remaining = separator >= 0 ? remaining[(separator + 1)..] : ReadOnlySpan<char>.Empty;
        }

        return false;
    }

    /// <summary>
    /// Adds <c>.md</c> when the last segment has no extension. A target that names one — <c>[[diagram.png]]</c> — is
    /// left alone and handled the way any other link to that file would be.
    /// </summary>
    private static string WithMarkdownExtension(ReadOnlySpan<char> path)
    {
        var separator = path.LastIndexOfAny('/', '\\');
        var name = separator >= 0 ? path[(separator + 1)..] : path;
        return name.Contains('.') ? path.ToString() : string.Concat(path, MarkdownExtension);
    }

    /// <summary>
    /// Percent-encodes each segment, so a space or an accent in a note's name survives as an address. The separators
    /// stay separators; a backslash becomes one, because a link is a URL even when the author typed a Windows path.
    /// </summary>
    private static string Encode(string path)
    {
        var segments = path.Split('/', '\\');
        for (var i = 0; i < segments.Length; i++)
        {
            segments[i] = Uri.EscapeDataString(segments[i]);
        }

        return string.Join('/', segments);
    }
}
