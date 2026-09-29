namespace MdReader.Web;

/// <summary>Which interactive widget a page carries above its prose. The prose renders without any of them.</summary>
internal enum SiteTool
{
    /// <summary>No widget: the page is prose and links.</summary>
    None,

    /// <summary>A Markdown box and a preview of it, both filled in by the server before the page is sent.</summary>
    Editor,

    /// <summary>The editor plus the row/column grid that writes a pipe table into it.</summary>
    TableBuilder,
}

/// <summary>
/// The schema.org type the page describes itself as. Only types the page's own content justifies are used: no
/// ratings, no prices beyond the one that is true (free), no invented authorship.
/// </summary>
internal enum SiteSchema
{
    /// <summary>A plain page, described only by its breadcrumb.</summary>
    WebPage,

    /// <summary>The product as a whole (the about page).</summary>
    SoftwareApplication,

    /// <summary>One tool that runs in the browser.</summary>
    WebApplication,

    /// <summary>A reference document (the cheat sheet).</summary>
    TechArticle,
}

/// <summary>One question and its answer. The answer is plain text so the page and its JSON-LD say the same words.</summary>
internal sealed record FaqItem(string Question, string Answer);

/// <summary>
/// A page the site serves: its URL, the text in its head, the Markdown file it renders as its body, and the widget it
/// carries. Everything a crawler reads comes from here; <see cref="SiteCatalog"/> holds the list.
/// </summary>
internal sealed record SitePage
{
    /// <summary>The URL path, starting with "/" and with no trailing slash.</summary>
    public required string Path { get; init; }

    /// <summary>The <c>&lt;title&gt;</c>.</summary>
    public required string Title { get; init; }

    /// <summary>The meta description, and the Open Graph / Twitter description.</summary>
    public required string Description { get; init; }

    /// <summary>The <c>&lt;h1&gt;</c>. Not the same as <see cref="Title"/>, which also carries the site name.</summary>
    public required string Heading { get; init; }

    /// <summary>The sentence under the heading.</summary>
    public required string Lead { get; init; }

    /// <summary>How the page is named in the footer's list and in its own breadcrumb.</summary>
    public required string NavLabel { get; init; }

    /// <summary>One line describing the page where another page links to it.</summary>
    public required string Blurb { get; init; }

    /// <summary>The Markdown file under <c>content/</c> that becomes the page body.</summary>
    public string? ContentFile { get; init; }

    /// <summary>The Markdown file under <c>content/</c> that fills the editor and its server-rendered preview.</summary>
    public string? SampleFile { get; init; }

    public SiteTool Tool { get; init; } = SiteTool.None;

    /// <summary>
    /// Puts a list of the body's own headings above it. Worth it on a long reference page and noise on a short one,
    /// so it is opt-in rather than automatic.
    /// </summary>
    public bool ShowContents { get; init; }

    /// <summary>Adds the button that prints the preview on its own (the browser's dialog writes the PDF).</summary>
    public bool PrintButton { get; init; }

    /// <summary>Shown on the page as a list of questions, and repeated as <c>FAQPage</c> structured data.</summary>
    public IReadOnlyList<FaqItem> Faqs { get; init; } = [];

    public SiteSchema Schema { get; init; } = SiteSchema.WebPage;

    /// <summary>The day the page was written, as <c>yyyy-MM-dd</c>, for the schema types that ask for one.</summary>
    public string? Published { get; init; }

    /// <summary>True for the tool pages, which list each other under "Other tools".</summary>
    public bool IsTool => Tool != SiteTool.None;
}
