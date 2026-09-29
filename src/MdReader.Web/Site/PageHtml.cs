using System.Text;

namespace MdReader.Web;

/// <summary>
/// Turns a <see cref="SitePage"/> into the document a browser and a crawler receive.
/// </summary>
/// <remarks>
/// <para>Everything that matters is in the HTML before any script runs: the heading, the prose, the questions, the
/// sample document in the editor <em>and</em> the rendered result beside it. With scripting off, a tool page is a
/// worked example of the tool rather than an empty shell, which is also exactly what a crawler is served.</para>
/// <para>These pages are additions and never modifications. They are built here, they load their own stylesheet and
/// scripts from <c>/site/</c>, and they read the reader's stylesheets and shared modules without changing a byte of
/// <c>web/</c> or <c>webapp/</c>. The reader at <c>/</c> is untouched: it links to none of this, and nothing here
/// composes it.</para>
/// <para>The page adds no inline script and no third-party request, so the content security policy in
/// <see cref="SecurityHeaders"/> is unchanged. The one <c>&lt;script&gt;</c> element written inline holds JSON-LD,
/// which browsers treat as data and never execute, so <c>script-src 'self'</c> does not apply to it.</para>
/// </remarks>
internal static class PageHtml
{
    private const string Brand = "MdReader";
    private const string Repository = "https://github.com/quanrealvn/mdreader";

    public static string Build(SitePage page, SiteOptions options, RenderedMarkdown? body, RenderedMarkdown? sample)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(options);

        var bodyHtml = body is null ? string.Empty : SiteImages.Expand(body.Html);
        var canonical = options.Absolute(page.Path);
        var builder = new StringBuilder(32 * 1024);

        builder.Append("<!doctype html>\n<html lang=\"en\" data-theme=\"light\">\n<head>\n");
        WriteHead(builder, page, options, canonical, bodyHtml, sample);
        // The printable class is what site.css hangs its @media print rules on, so printing the PDF page leaves only
        // the rendered document on the sheet whether the reader used the button or pressed Ctrl+P.
        builder.Append("</head>\n<body class=\"mdr-site")
            .Append(page.PrintButton ? " mdr-site-printable" : string.Empty)
            .Append("\">\n");

        WriteHeader(builder);
        builder.Append("<main id=\"content\" class=\"mdr-site-main\">\n");
        WriteIntro(builder, page);

        if (page.Tool != SiteTool.None)
        {
            WriteTool(builder, page, sample);
        }

        if (page.ShowContents && body is not null)
        {
            WriteContents(builder, body);
        }

        if (bodyHtml.Length > 0)
        {
            builder.Append("<div class=\"markdown-body mdr-site-prose\">\n").Append(bodyHtml).Append("\n</div>\n");
        }

        if (page.Path == SiteCatalog.AboutPath)
        {
            WriteToolList(builder, "Tools", SiteCatalog.Tools.Append(SiteCatalog.ByPath(SiteCatalog.CheatSheetPath)));
        }
        else if (page.IsTool)
        {
            WriteToolList(builder, "Other tools", SiteCatalog.Tools
                .Where(other => other.Path != page.Path)
                .Append(SiteCatalog.ByPath(SiteCatalog.CheatSheetPath)));
        }

        WriteFaq(builder, page);
        builder.Append("</main>\n");
        WriteFooter(builder);
        builder.Append("</body>\n</html>\n");
        return builder.ToString();
    }

    // -----------------------------------------------------------------------------------------------------------
    // head
    // -----------------------------------------------------------------------------------------------------------

    private static void WriteHead(StringBuilder builder, SitePage page, SiteOptions options, string canonical,
        string bodyHtml, RenderedMarkdown? sample)
    {
        var description = HtmlText.Escape(page.Description);
        var title = HtmlText.Escape(page.Title);
        var image = options.Absolute(SiteImages.OpenGraph);

        builder.Append("<meta charset=\"utf-8\">\n")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n")
            .Append("<title>").Append(title).Append("</title>\n")
            .Append("<meta name=\"description\" content=\"").Append(description).Append("\">\n")
            .Append("<link rel=\"canonical\" href=\"").Append(canonical).Append("\">\n")
            .Append("<meta name=\"color-scheme\" content=\"light dark\">\n");

        if (options.GoogleSiteVerification is { Length: > 0 } token)
        {
            builder.Append("<meta name=\"google-site-verification\" content=\"")
                .Append(HtmlText.Escape(token)).Append("\">\n");
        }

        builder.Append("<meta property=\"og:type\" content=\"")
                .Append(page.Schema == SiteSchema.TechArticle ? "article" : "website").Append("\">\n")
            .Append("<meta property=\"og:site_name\" content=\"").Append(Brand).Append("\">\n")
            .Append("<meta property=\"og:locale\" content=\"en_US\">\n")
            .Append("<meta property=\"og:title\" content=\"").Append(title).Append("\">\n")
            .Append("<meta property=\"og:description\" content=\"").Append(description).Append("\">\n")
            .Append("<meta property=\"og:url\" content=\"").Append(canonical).Append("\">\n")
            .Append("<meta property=\"og:image\" content=\"").Append(image).Append("\">\n")
            .Append("<meta property=\"og:image:width\" content=\"").Append(SiteImages.OpenGraphWidth).Append("\">\n")
            .Append("<meta property=\"og:image:height\" content=\"").Append(SiteImages.OpenGraphHeight).Append("\">\n")
            .Append("<meta property=\"og:image:alt\" content=\"").Append(HtmlText.Escape(SiteImages.OpenGraphAlt))
                .Append("\">\n")
            .Append("<meta name=\"twitter:card\" content=\"summary_large_image\">\n")
            .Append("<meta name=\"twitter:title\" content=\"").Append(title).Append("\">\n")
            .Append("<meta name=\"twitter:description\" content=\"").Append(description).Append("\">\n")
            .Append("<meta name=\"twitter:image\" content=\"").Append(image).Append("\">\n")
            .Append("<meta name=\"twitter:image:alt\" content=\"").Append(HtmlText.Escape(SiteImages.OpenGraphAlt))
                .Append("\">\n")
            .Append("<link rel=\"icon\" href=\"/img/logo.svg\" type=\"image/svg+xml\">\n");

        foreach (var stylesheet in Stylesheets(bodyHtml, sample, page.PrintButton))
        {
            builder.Append("<link rel=\"stylesheet\" href=\"").Append(stylesheet).Append("\">\n");
        }

        foreach (var script in Scripts())
        {
            builder.Append(script).Append('\n');
        }

        foreach (var block in StructuredData.ForPage(page, options))
        {
            builder.Append("<script type=\"application/ld+json\">").Append(block).Append("</script>\n");
        }
    }

    /// <summary>
    /// The stylesheets this page actually needs, in cascade order. A page with no alert in it does not pay for the
    /// alert styles, and only the PDF page loads the print rules.
    /// </summary>
    /// <remarks>
    /// All but the last are the reader's own files, linked rather than copied: the rendered Markdown on these pages
    /// came out of the same renderer, so it has to be painted by the same rules or the preview would be a lie. They
    /// are named one by one instead of through <c>app.css</c>, which reaches them with <c>@import</c> — a browser can
    /// only act on that once it has fetched and parsed the file, so linking them here starts all of them at once.
    /// </remarks>
    private static IEnumerable<string> Stylesheets(string bodyHtml, RenderedMarkdown? sample, bool printButton)
    {
        var rendered = bodyHtml + (sample?.Html ?? string.Empty);
        yield return "/css/tokens.css";

        if (rendered.Length > 0)
        {
            yield return "/css/markdown.css";
            yield return "/css/code.css";
            if (rendered.Contains("markdown-alert", StringComparison.Ordinal))
            {
                yield return "/css/alerts.css";
            }
        }

        if (printButton)
        {
            yield return "/css/print.css";
        }

        yield return SiteAssets.Stylesheet;
    }

    /// <summary>
    /// The scripts, as written. The classic one runs before the first paint and sets the attributes the stylesheets
    /// read, so nobody watches a dark page turn light or the layout shift as a control is revealed.
    /// </summary>
    private static IEnumerable<string> Scripts()
    {
        yield return "<script src=\"" + SiteAssets.InitScript + "\"></script>";
        yield return "<script type=\"module\" src=\"" + SiteAssets.Script + "\"></script>";
    }

    // -----------------------------------------------------------------------------------------------------------
    // chrome
    // -----------------------------------------------------------------------------------------------------------

    private static void WriteHeader(StringBuilder builder)
    {
        builder.Append("<a class=\"mdr-site-skip\" href=\"#content\">Skip to the content</a>\n")
            .Append("<header class=\"mdr-site-header\">\n")
            .Append("<a class=\"mdr-site-brand\" href=\"").Append(SiteCatalog.ReaderPath).Append("\">")
            .Append(Logo)
            .Append("<span>").Append(Brand).Append("</span></a>\n")
            .Append("<nav class=\"mdr-site-nav\" aria-label=\"Main\">\n")
            .Append("<a href=\"").Append(SiteCatalog.AboutPath).Append("\">About</a>\n")
            .Append("<a href=\"").Append(SiteCatalog.CheatSheetPath).Append("\">Cheat sheet</a>\n")
            .Append("<a href=\"").Append(Repository).Append("\" rel=\"noopener\">GitHub</a>\n")
            .Append("<button id=\"mdr-site-theme\" class=\"mdr-site-theme\" type=\"button\" hidden>Dark</button>\n")
            .Append("<a class=\"mdr-site-button mdr-site-button--primary\" href=\"").Append(SiteCatalog.ReaderPath)
            .Append("\">Open the reader</a>\n")
            .Append("</nav>\n</header>\n");
    }

    /// <summary>
    /// The breadcrumb, the heading and the lead. Every page here is one level under the reader, which is the site
    /// root, so every page gets a breadcrumb — including the one that introduces the rest.
    /// </summary>
    private static void WriteIntro(StringBuilder builder, SitePage page)
    {
        builder.Append("<nav class=\"mdr-site-crumbs\" aria-label=\"Breadcrumb\">\n<ol>\n")
            .Append("<li><a href=\"").Append(SiteCatalog.ReaderPath).Append("\">").Append(Brand).Append("</a></li>\n")
            .Append("<li aria-current=\"page\">").Append(HtmlText.Escape(page.NavLabel)).Append("</li>\n")
            .Append("</ol>\n</nav>\n")
            .Append("<h1>").Append(HtmlText.Escape(page.Heading)).Append("</h1>\n")
            .Append("<p class=\"mdr-site-lead\">").Append(HtmlText.Escape(page.Lead)).Append("</p>\n");
    }

    private static void WriteFooter(StringBuilder builder)
    {
        builder.Append("<footer class=\"mdr-site-footer\">\n<div class=\"mdr-site-footer-cols\">\n")
            .Append("<nav aria-labelledby=\"footer-tools\">\n")
            .Append("<h2 id=\"footer-tools\">Tools</h2>\n<ul>\n");

        foreach (var tool in SiteCatalog.Tools)
        {
            builder.Append("<li><a href=\"").Append(tool.Path).Append("\">")
                .Append(HtmlText.Escape(tool.NavLabel)).Append("</a></li>\n");
        }

        builder.Append("</ul>\n</nav>\n")
            .Append("<nav aria-labelledby=\"footer-site\">\n<h2 id=\"footer-site\">").Append(Brand).Append("</h2>\n<ul>\n")
            .Append("<li><a href=\"").Append(SiteCatalog.ReaderPath).Append("\">The reader</a></li>\n")
            .Append("<li><a href=\"").Append(SiteCatalog.AboutPath).Append("\">About MdReader</a></li>\n")
            .Append("<li><a href=\"").Append(SiteCatalog.CheatSheetPath).Append("\">Markdown cheat sheet</a></li>\n")
            .Append("<li><a href=\"").Append(Repository).Append("\" rel=\"noopener\">Source on GitHub</a></li>\n")
            .Append("</ul>\n</nav>\n</div>\n")
            .Append("<p class=\"mdr-site-colophon\">MdReader is open source under the MIT license. ")
            .Append("The pages you are reading are Markdown files put through the same renderer the reader uses.</p>\n")
            .Append("</footer>\n");
    }

    // -----------------------------------------------------------------------------------------------------------
    // blocks
    // -----------------------------------------------------------------------------------------------------------

    private static void WriteToolList(StringBuilder builder, string heading, IEnumerable<SitePage> pages)
    {
        var id = heading.ToLowerInvariant().Replace(' ', '-');
        builder.Append("<nav class=\"mdr-site-cards\" aria-labelledby=\"").Append(id).Append("\">\n")
            .Append("<h2 id=\"").Append(id).Append("\">").Append(HtmlText.Escape(heading)).Append("</h2>\n<ul>\n");

        foreach (var page in pages)
        {
            builder.Append("<li><a href=\"").Append(page.Path).Append("\">")
                .Append("<span class=\"mdr-site-card-name\">").Append(HtmlText.Escape(page.NavLabel)).Append("</span>")
                .Append("<span class=\"mdr-site-card-blurb\">").Append(HtmlText.Escape(page.Blurb)).Append("</span>")
                .Append("</a></li>\n");
        }

        builder.Append("</ul>\n</nav>\n");
    }

    /// <summary>
    /// The body's own top-level headings, as links. The ids come from the renderer, which is the same thing that
    /// wrote the headings, so the list cannot point at an anchor that is not there.
    /// </summary>
    private static void WriteContents(StringBuilder builder, RenderedMarkdown body)
    {
        var sections = body.Toc.Where(entry => entry.Level == 2).ToList();
        if (sections.Count < 4)
        {
            return;
        }

        builder.Append("<nav class=\"mdr-site-contents\" aria-labelledby=\"on-this-page\">\n")
            .Append("<h2 id=\"on-this-page\">On this page</h2>\n<ul>\n");

        foreach (var entry in sections)
        {
            builder.Append("<li><a href=\"#").Append(HtmlText.Escape(entry.Id)).Append("\">")
                .Append(HtmlText.Escape(entry.Text)).Append("</a></li>\n");
        }

        builder.Append("</ul>\n</nav>\n");
    }

    private static void WriteFaq(StringBuilder builder, SitePage page)
    {
        if (page.Faqs.Count == 0)
        {
            return;
        }

        builder.Append("<section class=\"mdr-site-faq\" aria-labelledby=\"questions\">\n")
            .Append("<h2 id=\"questions\">Questions</h2>\n");

        foreach (var item in page.Faqs)
        {
            builder.Append("<h3>").Append(HtmlText.Escape(item.Question)).Append("</h3>\n")
                .Append("<p>").Append(HtmlText.Escape(item.Answer)).Append("</p>\n");
        }

        builder.Append("</section>\n");
    }

    private static void WriteTool(StringBuilder builder, SitePage page, RenderedMarkdown? sample)
    {
        var source = HtmlText.Escape(sample?.Source ?? string.Empty);
        var preview = sample?.Html ?? string.Empty;
        if (preview.Length > 0)
        {
            preview = SiteImages.Expand(preview);
        }

        builder.Append("<section class=\"mdr-tool\" aria-label=\"")
            .Append(HtmlText.Escape(page.Heading)).Append("\">\n");

        if (page.Tool == SiteTool.TableBuilder)
        {
            WriteTableBuilder(builder);
        }

        builder.Append("<div class=\"mdr-tool-panes\">\n")
            .Append("<div class=\"mdr-tool-pane\">\n")
            .Append("<div class=\"mdr-tool-head\"><label for=\"mdr-tool-input\">Markdown</label>")
            .Append("<span class=\"mdr-tool-actions\">")
            .Append("<button id=\"mdr-tool-open\" class=\"mdr-site-button mdr-site-button--small\" type=\"button\" hidden>Open a file</button>")
            .Append("<button id=\"mdr-tool-copy\" class=\"mdr-site-button mdr-site-button--small\" type=\"button\" hidden>Copy</button>")
            .Append("</span></div>\n")
            .Append("<textarea id=\"mdr-tool-input\" class=\"mdr-tool-input\" rows=\"16\" spellcheck=\"false\" ")
            .Append("autocomplete=\"off\" autocapitalize=\"off\" autocorrect=\"off\">")
            .Append(source)
            .Append("</textarea>\n")
            .Append("<input id=\"mdr-tool-file\" type=\"file\" accept=\".md,.markdown,.mdown,.mkd,.txt,text/markdown,text/plain\" hidden>\n")
            .Append("</div>\n")
            .Append("<div class=\"mdr-tool-pane\">\n")
            .Append("<div class=\"mdr-tool-head\"><span>Preview</span><span class=\"mdr-tool-actions\">");

        if (page.PrintButton)
        {
            builder.Append("<button id=\"mdr-tool-print\" class=\"mdr-site-button mdr-site-button--small\" type=\"button\" hidden>Save as PDF</button>");
        }

        builder.Append("</span></div>\n")
            .Append("<div class=\"mdr-tool-preview\"><article id=\"mdr-tool-output\" class=\"markdown-body\">\n")
            .Append(preview)
            .Append("\n</article></div>\n</div>\n</div>\n")
            .Append("<div class=\"mdr-tool-foot\">\n")
            .Append("<button id=\"mdr-tool-render\" class=\"mdr-site-button mdr-site-button--primary\" type=\"button\" hidden>")
            .Append("Update the preview</button>\n")
            .Append("<p id=\"mdr-tool-status\" class=\"mdr-tool-status\" role=\"status\"></p>\n")
            .Append("<a class=\"mdr-site-link\" href=\"").Append(SiteCatalog.ReaderPath)
            .Append("\">Open the full reader, with tabs and a contents panel</a>\n")
            .Append("</div>\n</section>\n");
    }

    private static void WriteTableBuilder(StringBuilder builder)
    {
        // Every control here needs scripting. site.css keeps the block out of the layout entirely when there is
        // none, and reserves its height when there is, so filling it in shifts nothing (see :root[data-js] there).
        // Either way the page still has the sample table in the editor below, rendered beside it.
        builder.Append("<div id=\"mdr-tool-builder\" class=\"mdr-tool-builder\">\n")
            .Append("<div class=\"mdr-tool-builder-bar\">\n")
            .Append("<span class=\"mdr-tool-builder-size\">")
            .Append("<button type=\"button\" data-grid=\"remove-row\" class=\"mdr-site-button mdr-site-button--small\" aria-label=\"Remove the last row\">Row &minus;</button>")
            .Append("<button type=\"button\" data-grid=\"add-row\" class=\"mdr-site-button mdr-site-button--small\" aria-label=\"Add a row\">Row +</button>")
            .Append("<button type=\"button\" data-grid=\"remove-column\" class=\"mdr-site-button mdr-site-button--small\" aria-label=\"Remove the last column\">Column &minus;</button>")
            .Append("<button type=\"button\" data-grid=\"add-column\" class=\"mdr-site-button mdr-site-button--small\" aria-label=\"Add a column\">Column +</button>")
            .Append("</span>\n</div>\n")
            .Append("<div class=\"mdr-tool-grid-scroll\"><table id=\"mdr-tool-grid\" class=\"mdr-tool-grid\"></table></div>\n")
            .Append("</div>\n");
    }

    /// <summary>The wordmark's inline SVG. Inline markup, not an inline script: the policy is unaffected.</summary>
    private const string Logo =
        "<svg class=\"mdr-site-logo\" viewBox=\"0 0 32 32\" width=\"24\" height=\"24\" aria-hidden=\"true\">"
        + "<rect x=\"1\" y=\"1\" width=\"30\" height=\"30\" rx=\"8\" fill=\"#2F81F7\"/>"
        + "<path fill=\"#fff\" d=\"M5 22V10h3l3 4 3-4h3v12h-3v-7l-3 4-3-4v7z\"/>"
        + "<path fill=\"#fff\" d=\"M21 10h3v6h2.5L22.5 22 18.5 16H21z\"/></svg>";
}
