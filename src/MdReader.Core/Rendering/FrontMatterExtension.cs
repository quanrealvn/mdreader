using System.Globalization;
using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Renderers;
using Markdig.Renderers.Html;

namespace MdReader.Core.Rendering;

/// <summary>
/// Renders YAML front matter as a header card instead of dropping it (ARCHITECTURE §4.1). Markdig's own
/// <c>UseYamlFrontMatter</c> registers a renderer that writes nothing; this extension puts ours in front of it.
/// </summary>
internal sealed class FrontMatterExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        // The block parser comes from UseYamlFrontMatter; only the rendering changes here.
    }

    public void Setup(MarkdownPipeline pipeline, Markdig.Renderers.IMarkdownRenderer renderer)
    {
        if (renderer is HtmlRenderer html)
        {
            // First match wins in Markdig's renderer lookup, so inserting at the front replaces the no-op renderer
            // UseYamlFrontMatter installed without depending on its type being public.
            html.ObjectRenderers.Insert(0, new FrontMatterHtmlRenderer());
        }
    }
}

/// <summary>
/// Writes the front-matter card: title, description, date and author, tags, and everything else as a key/value list
/// that collapses when it is long.
/// </summary>
/// <remarks>
/// Every class here is <c>markdown-frontmatter…</c>, never <c>mdr-…</c>: the sanitizer strips <c>mdr-</c> classes from
/// content (§8.2), and the alert markup already uses the <c>markdown-</c> prefix for the same reason. Only tags that
/// §8.2 allows are written, so the card survives sanitization unchanged, and every value goes out through
/// <see cref="HtmlRenderer.WriteEscape(string?)"/> — a value can't smuggle markup into the page.
/// </remarks>
internal sealed class FrontMatterHtmlRenderer : HtmlObjectRenderer<YamlFrontMatterBlock>
{
    /// <summary>More fields than this and the list starts collapsed.</summary>
    internal const int CollapseThreshold = 6;

    private static readonly string[] DescriptionKeys = ["description", "summary", "subtitle", "excerpt", "abstract"];
    private static readonly string[] DateKeys = ["date", "published", "created", "updated", "lastmod", "last_modified", "modified"];
    private static readonly string[] AuthorKeys = ["author", "authors", "by"];
    private static readonly string[] TagKeys = ["tags", "keywords", "categories", "topics"];

    protected override void Write(HtmlRenderer renderer, YamlFrontMatterBlock block)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(block);

        var data = FrontMatterParser.Parse(ReadLines(block));
        if (data.IsEmpty)
        {
            return;
        }

        var model = HeaderModel.From(data);

        renderer.Write("<div class=\"markdown-frontmatter\">\n");
        WriteHeadline(renderer, model);
        WriteDetails(renderer, model, data);
        renderer.Write("</div>\n");
    }

    private static void WriteHeadline(HtmlRenderer renderer, HeaderModel model)
    {
        if (model.Title is { } title)
        {
            renderer.Write("<p class=\"markdown-frontmatter-title\">").WriteEscape(title).Write("</p>\n");
        }

        if (model.Description is { } description)
        {
            renderer.Write("<p class=\"markdown-frontmatter-description\">").WriteEscape(description).Write("</p>\n");
        }

        if (model.Date is not null || model.Authors.Count > 0)
        {
            renderer.Write("<p class=\"markdown-frontmatter-meta\">");
            if (model.Date is { } date)
            {
                renderer.Write("<time class=\"markdown-frontmatter-date\"");
                if (IsoDate(date) is { } iso)
                {
                    renderer.Write(" datetime=\"").WriteEscape(iso).Write('"');
                }

                renderer.Write('>').WriteEscape(date).Write("</time>");
            }

            foreach (var author in model.Authors)
            {
                renderer.Write("<span class=\"markdown-frontmatter-author\">").WriteEscape(author).Write("</span>");
            }

            renderer.Write("</p>\n");
        }

        if (model.Tags.Count > 0)
        {
            renderer.Write("<p class=\"markdown-frontmatter-tags\">");
            foreach (var tag in model.Tags)
            {
                renderer.Write("<span class=\"markdown-frontmatter-tag\">").WriteEscape(tag).Write("</span>");
            }

            renderer.Write("</p>\n");
        }
    }

    private static void WriteDetails(HtmlRenderer renderer, HeaderModel model, FrontMatterData data)
    {
        var hasRaw = !string.IsNullOrEmpty(data.Raw);
        if (model.Fields.Count == 0 && !hasRaw && !data.Truncated)
        {
            return;
        }

        var open = model.Fields.Count <= CollapseThreshold && !hasRaw;
        renderer.Write("<details class=\"markdown-frontmatter-more\"");
        if (open)
        {
            renderer.Write(" open");
        }

        renderer.Write("><summary>").WriteEscape(SummaryText(model, hasRaw)).Write("</summary>\n");

        if (model.Fields.Count > 0)
        {
            renderer.Write("<dl class=\"markdown-frontmatter-fields\">\n");
            foreach (var field in model.Fields)
            {
                renderer.Write("<dt>").WriteEscape(field.Key).Write("</dt>\n<dd>");
                WriteValues(renderer, field);
                renderer.Write("</dd>\n");
            }

            renderer.Write("</dl>\n");
        }

        if (data.Truncated)
        {
            renderer.Write("<p class=\"markdown-frontmatter-note\">This front matter is longer than MdReader shows.</p>\n");
        }

        if (hasRaw)
        {
            renderer.Write("<pre class=\"markdown-frontmatter-raw\">\n").WriteEscape(data.Raw!).Write("</pre>\n");
        }

        renderer.Write("</details>\n");
    }

    private static void WriteValues(HtmlRenderer renderer, FrontMatterEntry field)
    {
        if (!field.IsSequence)
        {
            renderer.WriteEscape(field.Values[0]);
            return;
        }

        for (var i = 0; i < field.Values.Count; i++)
        {
            if (i > 0)
            {
                renderer.Write("<span class=\"markdown-frontmatter-sep\">, </span>");
            }

            renderer.WriteEscape(field.Values[i]);
        }
    }

    private static string SummaryText(HeaderModel model, bool hasRaw)
    {
        if (!model.HasHeadline)
        {
            return "Front matter";
        }

        if (hasRaw && model.Fields.Count == 0)
        {
            return "Front matter as written";
        }

        return model.Fields.Count == 1
            ? "1 more field"
            : string.Create(CultureInfo.InvariantCulture, $"{model.Fields.Count} more fields");
    }

    /// <summary>The value as a machine-readable <c>datetime</c>, or null when it isn't a date MdReader recognizes.</summary>
    internal static string? IsoDate(string value)
    {
        // Only the ISO shapes front matter actually uses; anything else stays plain text rather than being guessed at.
        if (value.Length < 10 || value[4] != '-' || value[7] != '-')
        {
            return null;
        }

        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
            ? value
            : null;
    }

    private static List<string> ReadLines(YamlFrontMatterBlock block)
    {
        var count = block.Lines.Count;
        var lines = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            lines.Add(block.Lines.Lines[i].Slice.ToString());
        }

        return lines;
    }

    /// <summary>The headline fields picked out of the parsed entries, plus everything that is left over.</summary>
    private sealed record HeaderModel(
        string? Title,
        string? Description,
        string? Date,
        IReadOnlyList<string> Authors,
        IReadOnlyList<string> Tags,
        IReadOnlyList<FrontMatterEntry> Fields)
    {
        public bool HasHeadline => Title is not null || Description is not null || Date is not null
                                   || Authors.Count > 0 || Tags.Count > 0;

        public static HeaderModel From(FrontMatterData data)
        {
            string? title = null;
            string? description = null;
            string? date = null;
            List<string> authors = [];
            List<string> tags = [];
            List<FrontMatterEntry> fields = [];

            foreach (var entry in data.Entries)
            {
                // Only top-level keys are headline material: a nested "book.title" is a field, not the document title.
                if (entry.Key.Contains('.', StringComparison.Ordinal) || entry.Values is [""])
                {
                    AddField(fields, entry);
                    continue;
                }

                if (title is null && Is(entry.Key, "title"))
                {
                    title = entry.Values[0];
                }
                else if (description is null && IsAny(entry.Key, DescriptionKeys))
                {
                    description = entry.Values[0];
                }
                else if (date is null && IsAny(entry.Key, DateKeys) && !entry.IsSequence)
                {
                    date = entry.Values[0];
                }
                else if (authors.Count == 0 && IsAny(entry.Key, AuthorKeys))
                {
                    authors.AddRange(entry.Values);
                }
                else if (tags.Count == 0 && IsAny(entry.Key, TagKeys))
                {
                    tags.AddRange(entry.Values);
                }
                else
                {
                    AddField(fields, entry);
                }
            }

            return new HeaderModel(title, description, date, authors, tags, fields);
        }

        private static void AddField(List<FrontMatterEntry> fields, FrontMatterEntry entry) => fields.Add(entry);

        private static bool Is(string key, string name) => string.Equals(key, name, StringComparison.OrdinalIgnoreCase);

        private static bool IsAny(string key, string[] names)
        {
            foreach (var name in names)
            {
                if (Is(key, name))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
