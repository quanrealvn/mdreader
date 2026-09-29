namespace MdReader.Web;

/// <summary>
/// The routes for the public pages, <c>robots.txt</c> and <c>sitemap.xml</c>.
/// </summary>
/// <remarks>
/// HEAD is mapped alongside GET, because routing answers neither from the other and a 405 on HEAD reads to a link
/// checker as a broken URL. The result writes no body for a HEAD request, only the headers. Routing does match a
/// trailing slash against the same template, which would give every page two URLs; <see cref="CanonicalHost"/>
/// redirects that spelling away before routing ever sees it.
/// </remarks>
internal static class SiteEndpoints
{
    private static readonly string[] ReadMethods = [HttpMethods.Get, HttpMethods.Head];

    public static void MapSite(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        foreach (var page in SiteCatalog.Pages)
        {
            var current = page;
            endpoints.MapMethods(current.Path, ReadMethods, (HttpContext context, SiteDocuments documents) =>
                Send(context, documents.Page(current)));
        }

        endpoints.MapMethods("/robots.txt", ReadMethods, (HttpContext context, SiteDocuments documents) =>
            Send(context, documents.Robots, CrawlerFile));

        endpoints.MapMethods("/sitemap.xml", ReadMethods, (HttpContext context, SiteDocuments documents) =>
            Send(context, documents.Sitemap, CrawlerFile));
    }

    /// <summary>
    /// Revalidate every time, and answer a revalidation with a 304. The pages carry no hash in their URLs, so a
    /// cached copy that outlived a deploy would be a copy nothing could replace.
    /// </summary>
    private const string RevalidateAlways = "no-cache";

    /// <summary>
    /// What robots.txt and sitemap.xml get instead. Only a crawler reads them, only a deploy changes them, and a
    /// reader who somehow sees an hour-old copy loses nothing. Being cacheable is the point: without it every crawl
    /// reaches the origin, and the machine this runs on sleeps when nobody is using it, so the file a search engine
    /// depends on is exactly the one that waits for a cold start.
    /// </summary>
    private const string CrawlerFile = "public, max-age=3600, stale-while-revalidate=86400";

    private static IResult Send(HttpContext context, SiteResponse response, string cacheControl = RevalidateAlways)
    {
        context.Response.Headers.CacheControl = cacheControl;
        return Results.Bytes(response.Bytes, response.ContentType, entityTag: response.ETag);
    }
}
