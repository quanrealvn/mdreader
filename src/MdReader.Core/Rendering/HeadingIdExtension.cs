using System.Globalization;
using System.Runtime.InteropServices;
using Markdig;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MdReader.Core.Rendering;

/// <summary>
/// GitHub-style heading ids (ARCHITECTURE §4.1 "Heading ids"): a drop-in replacement for Markdig's
/// <c>UseAutoIdentifiers(AutoIdentifierOptions.GitHub)</c> that produces exactly the same ids in linear time (§15).
/// </summary>
/// <remarks>
/// <para>Markdig numbers a duplicate by trying <c>base-1</c>, <c>base-2</c>, … from 1 every time, so n equal headings cost
/// O(n²): 50,000 <c># a</c> lines (200 KB) took ~46 s to parse, uncancellably. Here each base remembers the last suffix
/// it handed out. The set of used ids only grows, so every suffix tried before is still taken and the search can resume
/// after it: the first free suffix is the same one Markdig finds, and every failed probe hits a distinct used id, so the
/// whole document costs O(total heading length).</para>
/// <para>Everything else mirrors Markdig 1.4.0's AutoIdentifierExtension: the hooks (heading and setext paragraph
/// parsers' <c>Closed</c>, then the heading's <c>ProcessInlinesEnd</c>, so ids are assigned in inline-processing order),
/// the slug (inline tree rendered by a plain <see cref="HtmlRenderer"/> with HTML and escaping disabled, then
/// <see cref="LinkHelper.UrilizeAsGfm(ReadOnlySpan{char})"/>), <c>section</c> for an empty slug, and the collision
/// semantics (<c>hello-world-1</c> after three "Hello World"s → <c>hello-world-1-1</c>). The GitHub option has no
/// AutoLink, so no link reference definitions are registered. The 1,000-character cap and
/// <see cref="ContentIdPolicy"/> are applied later, in the AST pass and the sanitizer, as before.</para>
/// </remarks>
internal sealed class HeadingIdExtension : IMarkdownExtension
{
    /// <summary>The id of a heading whose slug is empty (e.g. "🚀" or "!!!").</summary>
    internal const string EmptySlugId = "section";

    /// <summary>Key of the per-document <see cref="HeadingIdState"/> (document data).</summary>
    private static readonly object StateKey = new();

    private static readonly ProcessBlockDelegate OnBlockClosedHandler = OnBlockClosed;
    private static readonly ProcessInlineDelegate AssignIdHandler = AssignId;

    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        if (pipeline.BlockParsers.Find<HeadingBlockParser>() is { } headingParser)
        {
            headingParser.Closed -= OnBlockClosedHandler;
            headingParser.Closed += OnBlockClosedHandler;
        }

        // Setext headings are closed by the paragraph parser (as a HeadingBlock).
        if (pipeline.BlockParsers.FindExact<ParagraphBlockParser>() is { } paragraphParser)
        {
            paragraphParser.Closed -= OnBlockClosedHandler;
            paragraphParser.Closed += OnBlockClosedHandler;
        }
    }

    public void Setup(MarkdownPipeline pipeline, Markdig.Renderers.IMarkdownRenderer renderer)
    {
    }

    private static void OnBlockClosed(BlockProcessor processor, Block block)
    {
        if (block is HeadingBlock heading)
        {
            // The id needs the heading's inlines, which are processed after all blocks are parsed.
            heading.ProcessInlinesEnd += AssignIdHandler;
        }
    }

    private static void AssignId(InlineProcessor processor, Inline? inline)
    {
        var document = processor.Document;
        if (document.GetData(StateKey) is not HeadingIdState state)
        {
            state = new HeadingIdState();
            document.SetData(StateKey, state);
        }

        var heading = (HeadingBlock)processor.Block!;
        if (heading.Inline is null)
        {
            return;
        }

        var attributes = heading.GetAttributes();
        if (attributes.Id is not null)
        {
            return;                                             // an id set elsewhere is kept (as Markdig does)
        }

        var slug = state.Slugify(heading.Inline);
        attributes.Id = state.Reserve(slug.Length == 0 ? EmptySlugId : slug);
    }

    /// <summary>Per-document state: the ids handed out so far and, per base id, the last numeric suffix used.</summary>
    private sealed class HeadingIdState
    {
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        private readonly Dictionary<string, uint> _lastSuffix = new(StringComparer.Ordinal);
        private StripRenderer? _renderer;

        public string Slugify(ContainerInline inline) => (_renderer ??= new StripRenderer()).Slugify(inline);

        /// <summary>
        /// <paramref name="baseId"/> if unused, else the first unused <c>baseId-N</c> (N = 1, 2, …), exactly like Markdig;
        /// the search for a base resumes after the last suffix it returned (those ids are all still taken).
        /// </summary>
        public string Reserve(string baseId)
        {
            if (_ids.Add(baseId))
            {
                return baseId;
            }

            ref var suffix = ref CollectionsMarshal.GetValueRefOrAddDefault(_lastSuffix, baseId, out _);
            string id;
            do
            {
                suffix++;
                id = string.Concat(baseId, "-", suffix.ToString(CultureInfo.InvariantCulture));
            }
            while (!_ids.Add(id));

            return id;
        }
    }

    /// <summary>
    /// Markdig's "strip renderer": a default <see cref="HtmlRenderer"/> (no pipeline renderers) that writes only text,
    /// unescaped. One per document, reset before every heading.
    /// </summary>
    private sealed class StripRenderer : HtmlRenderer
    {
        public StripRenderer()
            : base(new StringWriter(CultureInfo.InvariantCulture))
        {
            EnableHtmlForInline = false;
            EnableHtmlEscape = false;
        }

        public string Slugify(ContainerInline inline)
        {
            Reset();
            Render(inline);
            return LinkHelper.UrilizeAsGfm(((StringWriter)Writer).GetStringBuilder().ToString());
        }
    }
}
