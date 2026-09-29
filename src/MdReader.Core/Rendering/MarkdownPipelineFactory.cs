using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Helpers;
using Markdig.Renderers;

namespace MdReader.Core.Rendering;

/// <summary>
/// Builds the one Markdig pipeline MdReader uses (ARCHITECTURE §4.1). The pipeline is immutable and cached statically.
/// Extensions are listed explicitly (not <c>UseAdvancedExtensions</c>). Raw HTML stays enabled: the whole output is
/// sanitized afterwards.
/// </summary>
internal static class MarkdownPipelineFactory
{
    private static readonly Lazy<MarkdownPipeline> PipelineLazy =
        new(static () => Create(autoLinks: true), LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<MarkdownPipeline> PipelineWithoutAutoLinksLazy =
        new(static () => Create(autoLinks: false), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>The shared pipeline.</summary>
    public static MarkdownPipeline Pipeline => PipelineLazy.Value;

    /// <summary>
    /// The same pipeline without the bare-autolink extension, for documents that exceed
    /// <see cref="RenderLimits.MaxAutolinkCandidatesPerBlock"/> (the extension is quadratic within a paragraph).
    /// </summary>
    public static MarkdownPipeline PipelineWithoutAutoLinks => PipelineWithoutAutoLinksLazy.Value;

    /// <summary>Builds a new pipeline. Callers should use <see cref="Pipeline"/> / <see cref="PipelineWithoutAutoLinks"/>.</summary>
    internal static MarkdownPipeline Create(bool autoLinks = true)
    {
        var builder = new MarkdownPipelineBuilder()
            .UseYamlFrontMatter()
            .UsePipeTables(new PipeTableOptions { UseGfmRules = true })
            .UseFootnotes()
            .Use<HeadingIdExtension>();         // = UseAutoIdentifiers(AutoIdentifierOptions.GitHub), in linear time
        if (autoLinks)
        {
            builder = builder.UseAutoLinks();
        }

        return builder
            .UseTaskLists()
            .Use<TaskListLineExtension>()       // data-line on the checkboxes the pipeline itself produces (§4.1)
            .UseEmphasisExtras(EmphasisExtraOptions.Strikethrough)
            .UseEmojiAndSmiley(enableSmileys: false)
            .UseMathematics()
            .UseDiagrams()
            .UseAlertBlocks(renderKind: WriteAlertTitle)
            .UseDefinitionLists()
            .Build();
    }

    /// <summary>
    /// The display title of an alert kind: the five GitHub kinds (case-insensitive) get their GitHub titles, any other kind
    /// is shown in title case (the kind text is plain text; the caller escapes it).
    /// </summary>
    internal static string GetAlertTitle(ReadOnlySpan<char> kind)
    {
        if (kind.Equals("note", StringComparison.OrdinalIgnoreCase))
        {
            return "Note";
        }

        if (kind.Equals("tip", StringComparison.OrdinalIgnoreCase))
        {
            return "Tip";
        }

        if (kind.Equals("important", StringComparison.OrdinalIgnoreCase))
        {
            return "Important";
        }

        if (kind.Equals("warning", StringComparison.OrdinalIgnoreCase))
        {
            return "Warning";
        }

        if (kind.Equals("caution", StringComparison.OrdinalIgnoreCase))
        {
            return "Caution";
        }

        if (kind.IsEmpty)
        {
            return string.Empty;
        }

        return string.Concat(kind[..1].ToString().ToUpperInvariant(), kind[1..].ToString().ToLowerInvariant());
    }

    /// <summary>
    /// Replaces Markdig's whole default title element (which embeds an inline SVG the sanitizer would strip). The icon
    /// comes from CSS instead (ARCHITECTURE §10).
    /// </summary>
    private static void WriteAlertTitle(HtmlRenderer renderer, StringSlice kind)
    {
        renderer.Write("<p class=\"markdown-alert-title\">");
        renderer.WriteEscape(GetAlertTitle(kind.AsSpan()));
        renderer.Write("</p>\n");
    }
}
