using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MdReader.Web;

/// <summary>
/// The JSON-LD blocks a page carries.
/// </summary>
/// <remarks>
/// <para>Every statement here has to be true of the page it sits on, because structured data that describes
/// something the reader cannot find is the one kind of markup that earns a manual penalty. So: no
/// <c>aggregateRating</c> (nobody has rated anything), no <c>review</c>, and the only price claimed is the one that
/// is real — free. A <c>FAQPage</c> block is written only from questions the page actually prints, word for word.</para>
/// <para>Written with <see cref="JsonNode"/> and the default encoder, which escapes <c>&lt;</c> and <c>&gt;</c>. A
/// closing script tag therefore cannot appear inside the block, whatever ends up in the text.</para>
/// </remarks>
internal static class StructuredData
{
    private const string Context = "https://schema.org";
    private const string SiteName = "MdReader";
    private const string Category = "UtilitiesApplication";
    private const string Repository = "https://github.com/quanrealvn/mdreader";
    private const string License = "https://opensource.org/license/mit";

    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.Default };

    /// <summary>The product version, read off the assembly so it cannot drift from the one that shipped.</summary>
    private static readonly string Version =
        typeof(StructuredData).Assembly.GetName().Version is { } version
            ? $"{version.Major}.{version.Minor}.{version.Build}"
            : "1.0.0";

    /// <summary>One string per <c>&lt;script type="application/ld+json"&gt;</c> block, in the order they are emitted.</summary>
    public static IReadOnlyList<string> ForPage(SitePage page, SiteOptions options)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(options);

        var url = options.Absolute(page.Path);
        var blocks = new List<JsonObject>(4);

        switch (page.Schema)
        {
            case SiteSchema.SoftwareApplication:
                blocks.Add(Application(page, options, url));
                blocks.Add(WebSite(options));
                break;
            case SiteSchema.WebApplication:
                blocks.Add(Application(page, options, url));
                break;
            case SiteSchema.TechArticle:
                blocks.Add(Article(page, options, url));
                break;
            case SiteSchema.WebPage:
            default:
                break;
        }

        // Every generated page sits one level under the reader, which is the site root and is not generated here, so
        // every one of them carries the same two-level breadcrumb.
        blocks.Add(Breadcrumb(page, options));

        if (page.Faqs.Count > 0)
        {
            blocks.Add(Faq(page));
        }

        return [.. blocks.Select(block => block.ToJsonString(Options))];
    }

    private static JsonObject Application(SitePage page, SiteOptions options, string url)
    {
        var isProduct = page.Schema == SiteSchema.SoftwareApplication;
        var json = new JsonObject
        {
            ["@context"] = Context,
            ["@type"] = isProduct ? "SoftwareApplication" : "WebApplication",
            ["name"] = isProduct ? SiteName : page.Heading,
            ["url"] = url,
            ["description"] = page.Description,
            ["applicationCategory"] = Category,
            ["operatingSystem"] = isProduct ? "Web browser, Windows 10, Windows 11" : "Web browser",
            ["browserRequirements"] = "The page renders without JavaScript. Diagrams, math and syntax "
                + "highlighting need it.",
            ["softwareVersion"] = Version,
            ["isAccessibleForFree"] = true,
            ["license"] = License,
            ["offers"] = new JsonObject
            {
                ["@type"] = "Offer",
                ["price"] = "0",
                ["priceCurrency"] = "USD",
            },
        };

        if (isProduct)
        {
            // The application's own address is the reader, not the page describing it. mainEntityOfPage is what ties
            // the two together, and it is the honest way round: "/" is the thing, "/about" is the page about it.
            json["url"] = options.Absolute(SiteCatalog.ReaderPath);
            json["mainEntityOfPage"] = new JsonObject { ["@type"] = "WebPage", ["@id"] = url };
            json["downloadUrl"] = Repository + "/releases/latest";
            json["softwareHelp"] = options.Absolute(SiteCatalog.CheatSheetPath);
            json["screenshot"] = options.Absolute(SiteImages.Reader);
            json["featureList"] = new JsonArray(
                "GitHub-flavored Markdown, including tables, task lists, footnotes and alerts",
                "Syntax highlighting, Mermaid diagrams and KaTeX math",
                "Light and dark themes",
                "A contents panel built from the document's headings",
                "Printing, and saving to PDF through the browser's print dialog");
        }
        else
        {
            json["isPartOf"] = new JsonObject
            {
                ["@type"] = "WebSite",
                ["name"] = SiteName,
                ["url"] = options.Absolute(SiteCatalog.ReaderPath),
            };
        }

        return json;
    }

    private static JsonObject WebSite(SiteOptions options) => new()
    {
        ["@context"] = Context,
        ["@type"] = "WebSite",
        ["name"] = SiteName,
        ["url"] = options.Absolute(SiteCatalog.ReaderPath),
        ["inLanguage"] = "en",
    };

    private static JsonObject Article(SitePage page, SiteOptions options, string url)
    {
        var json = new JsonObject
        {
            ["@context"] = Context,
            ["@type"] = "TechArticle",
            ["headline"] = page.Heading,
            ["description"] = page.Description,
            ["url"] = url,
            ["inLanguage"] = "en",
            ["mainEntityOfPage"] = new JsonObject { ["@type"] = "WebPage", ["@id"] = url },
            ["publisher"] = new JsonObject
            {
                ["@type"] = "Organization",
                ["name"] = SiteName,
                ["url"] = options.Absolute(SiteCatalog.ReaderPath),
            },
        };

        if (page.Published is { Length: > 0 } published)
        {
            json["datePublished"] = published;
        }

        return json;
    }

    private static JsonObject Breadcrumb(SitePage page, SiteOptions options)
    {
        // Two levels, which is all there is: the site, then this page. The last entry deliberately carries no "item" -
        // that is how a breadcrumb says "you are here" rather than linking the page to itself.
        var items = new JsonArray
        {
            new JsonObject
            {
                ["@type"] = "ListItem",
                ["position"] = 1,
                ["name"] = SiteName,
                ["item"] = options.Absolute(SiteCatalog.ReaderPath),
            },
            new JsonObject
            {
                ["@type"] = "ListItem",
                ["position"] = 2,
                ["name"] = page.NavLabel,
            },
        };

        return new JsonObject
        {
            ["@context"] = Context,
            ["@type"] = "BreadcrumbList",
            ["itemListElement"] = items,
        };
    }

    private static JsonObject Faq(SitePage page)
    {
        var questions = new JsonArray();
        foreach (var item in page.Faqs)
        {
            questions.Add(new JsonObject
            {
                ["@type"] = "Question",
                ["name"] = item.Question,
                ["acceptedAnswer"] = new JsonObject
                {
                    ["@type"] = "Answer",
                    ["text"] = item.Answer,
                },
            });
        }

        return new JsonObject
        {
            ["@context"] = Context,
            ["@type"] = "FAQPage",
            ["mainEntity"] = questions,
        };
    }
}
