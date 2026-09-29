using Markdig;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Parsers.Inlines;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdReader.Core.Paths;

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
/// <para><c>.md</c> is appended unless the last segment already ends in a recognised file type, so a note called
/// <c>Release 1.2</c> keeps its name. The fragment is slugged by the same rule as a heading id, so
/// <c>#Getting started</c> finds <c>&lt;h2 id="getting-started"&gt;</c>.</para>
/// <para>A target that would leave the document's folder — absolute, rooted, a drive letter, a scheme, or any
/// <c>..</c> segment — is not turned into a link at all; the text stays as the author typed it.</para>
/// <para>That is deliberately stricter than the rest of the reader: <c>[x](../notes.md)</c> is an ordinary relative
/// link and <c>LinkClassifier</c> opens it, because a path someone wrote as a path is a path. <c>[[..]]</c> is a
/// different thing — a name, not a route — and an author reaching out of the folder through a name syntax is either
/// confused or hostile. Refusing costs a real document nothing, and text that was never a link is easier to
/// understand than a link that does nothing when it is clicked.</para>
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

        // Not while an ordinary link is still open. "[a [[b]] d](e.md)" is a link whose text happens to contain two
        // brackets, and emitting a finished link into the middle of one being built cuts it in half: the anchor ends
        // at "a ", the rest of the text falls outside it and "](e.md)" is swallowed. The document says something it
        // did not say. Declining here leaves the whole thing to the link parser, which renders it exactly as it did
        // before this extension existed.
        if (InsideAnUnclosedLink(processor))
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

    /// <summary>
    /// True while the ordinary link parser has an unclosed <c>[</c> on the go.
    /// </summary>
    /// <remarks>
    /// <para>Markdig builds a link in two halves: <c>[</c> leaves a <see cref="LinkDelimiterInline"/> behind, the
    /// text after it is parsed as ordinary inlines, and <c>](...)</c> later turns the delimiter and everything after
    /// it into the anchor. A finished link handed straight to the processor in the middle of that is not part of the
    /// text being collected, so the anchor closes early around whatever came before it.</para>
    /// <para>Only the ancestors are walked, and that is deliberate. A delimiter is a container, so everything parsed
    /// after the <c>[</c> is a child of it — walking the previous siblings as well finds nothing extra and costs a
    /// pass over every inline already in the paragraph, which makes this quadratic in the number of links. A first
    /// version did exactly that: 4,000 wiki links took 127 ms and 16,000 took 1,139 ms, against 38 ms and 168 ms for
    /// the same number of ordinary links. The walk is not bounded by anything — Markdig's 128-level nesting limit
    /// applies to rendered output, not to the open delimiter stack, and a paragraph of 2,000 unclosed brackets gives
    /// a spine 2,001 deep. It costs nothing in practice: at that depth Markdig's own work already dominates, and the
    /// guard measures the same either way (238 ms with it, 263 ms without, on 8,000 attempts at depth 3,000).</para>
    /// </remarks>
    private static bool InsideAnUnclosedLink(InlineProcessor processor)
    {
        for (Inline? inline = processor.Inline; inline is not null; inline = inline.Parent)
        {
            if (inline is LinkDelimiterInline)
            {
                return true;
            }
        }

        return false;
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

        // A heading of nothing but punctuation or emoji slugs to the empty string, and "Guide.md#" is a worse
        // answer than "Guide.md": a bare # is a link to the top of the page dressed up as a link to a section.
        var slug = heading.IsEmpty ? string.Empty : LinkHelper.UrilizeAsGfm(heading);
        var fragment = slug.Length == 0 ? string.Empty : FragmentSeparator + slug;

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

        // Walk the segments: ".." anywhere means somewhere else ("..." and "a..b" are ordinary names), and an empty
        // one means the target is not a file name at all. "[[a\]]" used to arrive here as "a\", whose last segment is
        // empty, and came out as a link to a file called ".md" inside a folder "a".
        var remaining = path;
        while (true)
        {
            var separator = remaining.IndexOfAny('/', '\\');
            var segment = separator >= 0 ? remaining[..separator] : remaining;
            if (segment.IsEmpty || segment.SequenceEqual(".."))
            {
                return true;
            }

            if (separator < 0)
            {
                break;
            }

            remaining = remaining[(separator + 1)..];
        }

        return false;
    }


    /// <summary>
    /// Adds <c>.md</c> unless the last segment already ends in a file type that is plainly a file type.
    /// </summary>
    /// <remarks>
    /// "Has a dot in it" is not the same question. A note is very often called <c>Release 1.2</c>, <c>Node.js</c> or
    /// <c>meeting 2026.09.29</c>, and treating the dot as an extension gave every one of them a link to a file that
    /// does not exist — which the classifier then refused to open, because only Markdown opens. A link that looks
    /// like a link and does nothing when clicked is the exact thing this feature is supposed not to produce.
    /// </remarks>
    private static string WithMarkdownExtension(ReadOnlySpan<char> path)
    {
        var separator = path.LastIndexOfAny('/', '\\');
        var name = separator >= 0 ? path[(separator + 1)..] : path;
        var dot = name.LastIndexOf('.');

        // dot > 0 so a name that merely starts with one (".profile") keeps its .md: the dot is part of the name.
        if (dot > 0 && IsAFileType(name[dot..]))
        {
            return path.ToString();
        }

        return string.Concat(path, MarkdownExtension);
    }

    /// <summary>
    /// The extensions a wiki link may name without meaning "the Markdown note called this". Markdown's own come
    /// from <see cref="MarkdownFileTypes"/> so the two can never disagree; the rest are the things people actually
    /// put beside their notes. Anything else is treated as part of the note's name.
    /// </summary>
    private static bool IsAFileType(ReadOnlySpan<char> extension)
    {
        foreach (var markdown in MarkdownFileTypes.Extensions)
        {
            if (extension.Equals(markdown, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var known in OtherFileTypes)
        {
            if (extension.Equals(known, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <remarks>
    /// Deliberately no source-code extensions. ".js" would make <c>[[Node.js]]</c> point at a JavaScript file rather
    /// than the note called "Node.js", and the reader opens only Markdown, so a link to <c>script.py</c> and a link
    /// to <c>script.py.md</c> are refused alike — the note reading is the one that can ever be right.
    /// </remarks>
    private static readonly string[] OtherFileTypes =
    [
        ".txt", ".rtf", ".pdf", ".csv", ".tsv", ".json", ".xml", ".yml", ".yaml", ".toml", ".ini", ".log", ".sql",
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".epub", ".ics", ".eml",
        ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".bmp", ".ico", ".avif", ".heic", ".tiff",
        ".mp3", ".wav", ".ogg", ".flac", ".m4a", ".mp4", ".webm", ".mov", ".mkv", ".avi",
        ".zip", ".gz", ".7z", ".tar", ".rar", ".bz2", ".xz", ".exe", ".msi", ".deb", ".rpm",
    ];

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
