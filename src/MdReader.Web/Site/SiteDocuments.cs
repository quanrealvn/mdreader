using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Net.Http.Headers;

namespace MdReader.Web;

/// <summary>A finished response: the bytes, what they are, and the tag that lets a browser skip re-fetching them.</summary>
internal sealed record SiteResponse(byte[] Bytes, string ContentType, EntityTagHeaderValue ETag);

/// <summary>
/// Builds every generated document the site serves — the pages, <c>robots.txt</c> and <c>sitemap.xml</c> — and keeps
/// them.
/// </summary>
/// <remarks>
/// Each one is built on the first request for it and then reused: the Markdown behind a page cannot change while the
/// server runs, so re-rendering it per request would only burn CPU on a machine that has one shared core. The ETag is
/// the hash of the bytes, so a deploy that changes a page invalidates exactly that page, and a revalidation of an
/// unchanged one costs a 304 with no body.
/// </remarks>
internal sealed class SiteDocuments(SiteContent content, SiteOptions options)
{
    private readonly ConcurrentDictionary<string, SiteResponse> _cache = new(StringComparer.Ordinal);

    public SiteResponse Page(SitePage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return _cache.GetOrAdd(page.Path, _ => Build(page));
    }

    public SiteResponse Robots => _cache.GetOrAdd("robots.txt", _ =>
        Response(RobotsText(), "text/plain; charset=utf-8"));

    public SiteResponse Sitemap => _cache.GetOrAdd("sitemap.xml", _ =>
        Response(SitemapXml(), "application/xml; charset=utf-8"));

    private SiteResponse Build(SitePage page)
    {
        var body = page.ContentFile is null ? null : content.Get(page.ContentFile);
        var sample = page.SampleFile is null ? null : content.Get(page.SampleFile);
        return Response(PageHtml.Build(page, options, body, sample), "text/html; charset=utf-8");
    }

    private string RobotsText()
    {
        // Everything is crawlable except the render endpoint, which is a POST API with nothing to index, and the
        // health check. Deep-linking the sitemap here is what points a crawler at the whole list from one file that
        // every crawler asks for first.
        var builder = new StringBuilder()
            .Append("User-agent: *\n")
            .Append("Allow: /\n")
            .Append("Disallow: /api/\n")
            .Append("Disallow: ").Append(CanonicalHost.HealthPath).Append('\n')
            .Append('\n')
            .Append("Sitemap: ").Append(options.Absolute("/sitemap.xml")).Append('\n');
        return builder.ToString();
    }

    private string SitemapXml()
    {
        // No lastmod, changefreq or priority. The first would be a date nothing here actually tracks, and the other
        // two have been ignored for years; a sitemap that states only what exists is one that cannot be wrong.
        // The list starts with "/" — the reader, which this project does not generate but which is still the site.
        var builder = new StringBuilder()
            .Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n")
            .Append("<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n");

        foreach (var path in SiteCatalog.SitemapPaths)
        {
            builder.Append("  <url><loc>").Append(options.Absolute(path)).Append("</loc></url>\n");
        }

        return builder.Append("</urlset>\n").ToString();
    }

    private static SiteResponse Response(string text, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var tag = Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
        return new SiteResponse(bytes, contentType, new EntityTagHeaderValue($"\"{tag}\""));
    }
}
