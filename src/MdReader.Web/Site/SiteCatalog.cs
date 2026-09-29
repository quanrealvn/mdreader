namespace MdReader.Web;

/// <summary>
/// Every page this project generates. It is the single list: the routes are mapped from it, the sitemap is written
/// from it, the footer links are built from it, and a test fails if the three ever disagree.
/// </summary>
/// <remarks>
/// The reader at <see cref="ReaderPath"/> is deliberately not in it. That page is <c>webapp/index.html</c>, served by
/// the static-file middleware exactly as it always was: nothing here composes it, wraps it, or adds a byte to it. The
/// sitemap still lists it, because it exists and it is the site's root.
/// </remarks>
internal static class SiteCatalog
{
    /// <summary>
    /// The reader. Not a page of this catalogue — it is a static file — but it is the site root, so the header, the
    /// breadcrumbs and the footer point at it and the sitemap lists it first.
    /// </summary>
    public const string ReaderPath = "/";

    /// <summary>The page that introduces MdReader: the site's own front door, one level under the reader.</summary>
    public const string AboutPath = "/about";

    public const string CheatSheetPath = "/markdown-cheat-sheet";

    public static IReadOnlyList<SitePage> Pages { get; } =
    [
        new SitePage
        {
            Path = AboutPath,
            Title = "About MdReader — what it renders, and where your text goes",
            Description = "What MdReader renders, where the text you paste goes, how raw HTML is handled, and what "
                + "the Windows app adds to the browser version.",
            Heading = "About MdReader",
            Lead = "MdReader renders Markdown in a browser tab and in a Windows app. Both use the same renderer, "
                + "so a document looks the same in either one.",
            NavLabel = "About",
            Blurb = "What MdReader is, and where the text you paste goes.",
            ContentFile = "about.md",
            Schema = SiteSchema.SoftwareApplication,
            Faqs =
            [
                new FaqItem(
                    "Is MdReader free?",
                    "Yes. The browser version is free to use and the Windows app is a free download. The source is "
                    + "on GitHub under the MIT license."),
                new FaqItem(
                    "Do I need an account?",
                    "No. There is nothing to sign up for and no sign-in."),
                new FaqItem(
                    "What happens to the text I paste?",
                    "The browser sends it to the server, which renders it and sends HTML back. It is held in memory "
                    + "for the length of that request and never written to disk. The log line for a render records "
                    + "how many characters went in, how many came back and how long it took, and nothing else. A "
                    + "request that is refused logs why, and nothing more."),
                new FaqItem(
                    "Can I open a file instead of pasting?",
                    "Yes. The reader takes a .md, .markdown or .txt file from the Open button or from a drop onto "
                    + "the window. The browser reads the file itself; the text then goes to the server to be "
                    + "rendered, the same way pasted text does."),
            ],
        },

        new SitePage
        {
            Path = "/markdown-preview",
            Title = "Markdown preview — see what your Markdown renders to",
            Description = "Paste Markdown and read the rendered page. Pipe tables, task lists, footnotes, alerts, "
                + "strikethrough, emoji, syntax highlighting, math and Mermaid diagrams.",
            Heading = "Markdown preview",
            Lead = "Put a document in the box and read the rendered page under it. The renderer is Markdig with the "
                + "GitHub extensions turned on.",
            NavLabel = "Markdown preview",
            Blurb = "See what a document renders to while you edit it.",
            ContentFile = "markdown-preview.md",
            SampleFile = "sample-preview.md",
            Tool = SiteTool.Editor,
            Schema = SiteSchema.WebApplication,
            Faqs =
            [
                new FaqItem(
                    "Which Markdown extensions are on?",
                    "CommonMark as implemented by Markdig, plus the extensions GitHub uses: pipe tables, task lists, "
                    + "strikethrough, bare autolinks, footnotes, definition lists, alert blocks, emoji shortcodes, "
                    + "math and Mermaid diagrams. YAML front matter is recognised and hidden."),
                new FaqItem(
                    "Can I use raw HTML in the document?",
                    "Within limits. The rendered page goes through an allow-list sanitizer, so details, summary, "
                    + "kbd, sup, sub and picture survive, and script, style, iframe, form and every on-event "
                    + "attribute are removed before the page is assembled."),
                new FaqItem(
                    "Is there a size limit?",
                    "Two megabytes per document. A larger one is refused with a message rather than being cut short."),
            ],
        },

        new SitePage
        {
            Path = "/readme-preview",
            Title = "README preview, the way GitHub will show it",
            Description = "Paste a README and see it rendered GitHub-style: alerts, tables, task lists, collapsed "
                + "details sections and the heading anchors GitHub builds.",
            Heading = "README preview",
            Lead = "Read a README the way the repository page will show it, before the commit that puts it there.",
            NavLabel = "README preview",
            Blurb = "Read a README before the commit that publishes it.",
            ContentFile = "readme-preview.md",
            SampleFile = "sample-readme.md",
            Tool = SiteTool.Editor,
            Schema = SiteSchema.WebApplication,
            Faqs =
            [
                new FaqItem(
                    "Do relative links and images work?",
                    "No, and they cannot: the server has no copy of your repository, so there is nothing for "
                    + "./docs/setup.md or images/logo.png to point at. Relative images are dropped and relative "
                    + "links are left in place but lead nowhere. An image with a full https address loads normally, "
                    + "which covers badges."),
                new FaqItem(
                    "Will it look exactly like GitHub?",
                    "Close, not identical. The same extensions are on and the stylesheet follows GitHub's, but "
                    + "GitHub also rewrites relative paths against the repository, adds its own anchor links beside "
                    + "headings, and applies its own filter to raw HTML."),
                new FaqItem(
                    "Does it render GitHub alerts?",
                    "Yes. A blockquote whose first line is [!NOTE], [!TIP], [!IMPORTANT], [!WARNING] or [!CAUTION] "
                    + "becomes the coloured callout, with the same five colours and icons."),
            ],
        },

        new SitePage
        {
            Path = "/markdown-to-pdf",
            Title = "Markdown to PDF, through your browser's print dialog",
            Description = "Render a Markdown file, look at it, then use your browser's print dialog to save it as a "
                + "PDF. Code, tables, diagrams and math come along.",
            Heading = "Markdown to PDF",
            Lead = "Render the document first, read it, then print it to PDF from the browser. There is no "
                + "conversion service in between.",
            NavLabel = "Markdown to PDF",
            Blurb = "Render a document and print it to PDF from the browser.",
            ContentFile = "markdown-to-pdf.md",
            SampleFile = "sample-pdf.md",
            Tool = SiteTool.Editor,
            PrintButton = true,
            Schema = SiteSchema.WebApplication,
            Faqs =
            [
                new FaqItem(
                    "Is my document uploaded to a conversion service?",
                    "No. The text goes to MdReader's own server, which renders it to HTML and sends it back. The PDF "
                    + "is written by your browser on your own machine."),
                new FaqItem(
                    "Does the PDF keep syntax highlighting and diagrams?",
                    "Code keeps its syntax colours, Mermaid diagrams print as vector drawings rather than pictures, "
                    + "and KaTeX math prints as selectable text. Block backgrounds only appear if you switch on your "
                    + "browser's background graphics option."),
                new FaqItem(
                    "Which page size and margins does it use?",
                    "Whichever ones you choose in the print dialog. MdReader sets no page size and no margins of its "
                    + "own, so the defaults are your browser's."),
                new FaqItem(
                    "How do I force a page break?",
                    "You cannot, and that is a real limitation. Markdown has no page-break syntax, and the HTML that "
                    + "could carry one is removed by the sanitizer. Printing does keep code blocks, tables and alerts "
                    + "from being split across two pages."),
            ],
        },

        new SitePage
        {
            Path = "/markdown-table-generator",
            Title = "Markdown table generator — build a table, copy the text",
            Description = "Set the rows, columns and alignment, fill in the cells, and copy the Markdown pipe table. "
                + "Paste a table you already have and the grid picks it up.",
            Heading = "Markdown table generator",
            Lead = "Type into the grid and the pipe table under it is written for you. Edit the text instead and the "
                + "grid follows.",
            NavLabel = "Table generator",
            Blurb = "Fill in a grid and copy the pipe table it writes.",
            ContentFile = "markdown-table-generator.md",
            SampleFile = "sample-table.md",
            Tool = SiteTool.TableBuilder,
            Schema = SiteSchema.WebApplication,
            Faqs =
            [
                new FaqItem(
                    "How do I put a pipe character inside a cell?",
                    "Escape it as a backslash followed by the pipe. A bare pipe would start the next cell."),
                new FaqItem(
                    "Can a cell hold more than one line?",
                    "Not as Markdown: a row of a pipe table has to stay on one line. A br tag inside the cell is the "
                    + "usual way around it, and it renders here."),
                new FaqItem(
                    "What does the row of dashes do?",
                    "It separates the header from the body, and it sets the alignment. A colon at the left of a "
                    + "column aligns it left, at the right aligns it right, and at both ends centres it."),
                new FaqItem(
                    "Do the columns have to line up in the source?",
                    "No. The padding that makes the source readable is ignored, so a table still renders if the "
                    + "pipes are ragged. Lining them up is only for whoever reads the file."),
            ],
        },

        new SitePage
        {
            Path = "/mermaid-live-editor",
            Title = "Mermaid live editor, in a Markdown document",
            Description = "Write Mermaid in a fenced code block and the diagram is drawn in the page. Mermaid 12 "
                + "runs in your browser, on text that is rendered as Markdown first.",
            Heading = "Mermaid live editor",
            Lead = "A code fence marked mermaid becomes a diagram. Mermaid 12 draws it in your browser, which is "
                + "also what happens in the desktop app.",
            NavLabel = "Mermaid editor",
            Blurb = "Write Mermaid in a code fence and watch the diagram.",
            ContentFile = "mermaid-live-editor.md",
            SampleFile = "sample-mermaid.md",
            Tool = SiteTool.Editor,
            Schema = SiteSchema.WebApplication,
            Faqs =
            [
                new FaqItem(
                    "Which Mermaid version is this?",
                    "Mermaid 12.0.0. It is bundled with the page, so nothing is fetched from a CDN and the version "
                    + "only changes when MdReader ships a new one."),
                new FaqItem(
                    "What happens when a diagram has a syntax error?",
                    "Mermaid's own error message appears where the diagram would have been, and the rest of the "
                    + "document still renders."),
                new FaqItem(
                    "Can I export the diagram as a file?",
                    "There is no export button. The diagram is an SVG in the page, so printing the page to PDF keeps "
                    + "it as a vector drawing rather than a picture of one."),
                new FaqItem(
                    "Does the diagram follow the dark theme?",
                    "Yes. Switching the theme redraws every diagram on the page with Mermaid's matching palette, and "
                    + "printing redraws them light so they are legible on paper."),
            ],
        },

        new SitePage
        {
            Path = "/katex-preview",
            Title = "KaTeX preview for math inside Markdown",
            Description = "Write math between dollar signs in Markdown and see it typeset by KaTeX, inline and as "
                + "display blocks, in your browser. Nothing is fetched from a CDN.",
            Heading = "KaTeX preview",
            Lead = "Markdown with math in it, typeset by KaTeX 0.18.7 in the page.",
            NavLabel = "KaTeX preview",
            Blurb = "Math between dollar signs, typeset as you write it.",
            ContentFile = "katex-preview.md",
            SampleFile = "sample-math.md",
            Tool = SiteTool.Editor,
            Schema = SiteSchema.WebApplication,
            Faqs =
            [
                new FaqItem(
                    "Which delimiters work?",
                    "One dollar sign on each side for inline math, two for a display block on its own lines. A "
                    + "dollar sign only opens math when the character before it is not a letter or a digit, and "
                    + "only closes it when the character after it is not one either. That is why prices usually "
                    + "survive: in \"$40 or $50\" the second sign is followed by a digit, so it cannot close. Put a "
                    + "backslash before a dollar sign when you want to be sure of it."),
                new FaqItem(
                    "Which KaTeX version is this?",
                    "KaTeX 0.18.7, bundled with the page together with its fonts. Nothing is fetched from a CDN."),
                new FaqItem(
                    "Does KaTeX understand everything LaTeX does?",
                    "No. KaTeX covers mathematical notation, not LaTeX as a document language, so there are no "
                    + "packages, no macros from a preamble and no figures. KaTeX's own supported-functions list is "
                    + "the reference. Anything it cannot parse is shown in red where it appears, and the rest of the "
                    + "document is unaffected."),
                new FaqItem(
                    "Is the math searchable and selectable?",
                    "Yes. KaTeX writes HTML and MathML rather than an image, so the notation can be selected, copied "
                    + "and read aloud by a screen reader."),
            ],
        },

        new SitePage
        {
            Path = CheatSheetPath,
            Title = "Markdown cheat sheet — syntax and what it renders to",
            Description = "Every piece of Markdown syntax with the source, then the result: headings, lists, "
                + "tables, links, code, footnotes, alerts, task lists, math and diagrams.",
            Heading = "Markdown cheat sheet",
            Lead = "Each piece of syntax, then what it turns into. This page is itself a Markdown file put "
                + "through MdReader's own renderer, so the examples below are not pictures of the answer. They "
                + "are the answer.",
            NavLabel = "Cheat sheet",
            Blurb = "The syntax, and what each piece of it renders to.",
            ContentFile = "markdown-cheat-sheet.md",
            Schema = SiteSchema.TechArticle,
            Published = "2026-09-28",
            ShowContents = true,
        },
    ];

    /// <summary>The pages that carry a tool, in the order they are listed to a reader.</summary>
    public static IReadOnlyList<SitePage> Tools { get; } = [.. Pages.Where(page => page.IsTool)];

    /// <summary>
    /// Every URL the sitemap names: the reader first, then the generated pages. The reader appears here and nowhere
    /// else in this file, because it is the one URL the site does not build.
    /// </summary>
    public static IReadOnlyList<string> SitemapPaths { get; } = [ReaderPath, .. Pages.Select(page => page.Path)];

    public static SitePage ByPath(string path) =>
        Pages.FirstOrDefault(page => page.Path == path)
        ?? throw new ArgumentOutOfRangeException(nameof(path), path, "No such page.");
}
