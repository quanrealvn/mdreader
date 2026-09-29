using System.Net;

namespace MdReader.Web;

/// <summary>
/// Makes every page answer at exactly one URL: one host name, and one spelling of the path.
/// </summary>
/// <remarks>
/// <para>The app answers on <c>mdreader.fly.dev</c> as well as on the domain, and that address is public knowledge:
/// it is in certificate transparency logs and in older copies of the README. Left alone it is a second, complete copy
/// of the site, and the two copies compete with each other. A permanent redirect folds one into the other. The
/// trailing slash is the same problem one path at a time — routing matches <c>/markdown-to-pdf/</c> and
/// <c>/markdown-to-pdf</c> against the same endpoint, so without this the page has two URLs.</para>
/// <para>The exemptions matter more than the redirect does, because getting one wrong takes the machine out of
/// rotation rather than merely costing a ranking:</para>
/// <list type="bullet">
/// <item><description><c>/healthz</c> is never redirected, whatever host it carries. fly.io's check
/// (<c>fly.toml</c>: <c>[[http_service.checks]]</c>, <c>GET /healthz</c> against the machine's internal port) is made
/// by the proxy against the machine itself, and what it puts in the <c>Host</c> header is not ours to rely on. A
/// health check that got a 301 would be read as a failure, and fly would stop routing to the machine.</description></item>
/// <item><description>A host with no dot in it (<c>localhost</c>, a container name), an IP literal, and anything
/// under fly's private <c>.internal</c> domain are left alone. That covers local runs, the test server (whose base
/// address is <c>http://localhost/</c>) and anything that reached the machine without going through public
/// DNS.</description></item>
/// </list>
/// <para>Requests that came through Cloudflare are not a special case. Cloudflare forwards the client's own
/// <c>Host</c>, so they arrive as <c>mdreader.online</c> and match on the first comparison. That is also what makes a
/// loop impossible: the only requests that are redirected are the ones whose host is not the one being redirected
/// to.</para>
/// </remarks>
internal static class CanonicalHost
{
    /// <summary>fly.io's health check, exempt whatever host it carries.</summary>
    internal const string HealthPath = "/healthz";

    private const string InternalSuffix = ".internal";

    /// <summary>
    /// Where a request should be sent instead, or null to let it through. Kept free of <see cref="HttpContext"/> so
    /// the rules can be tested one host name and one path at a time.
    /// </summary>
    /// <param name="host">The <c>Host</c> header's host part, without the port and without IPv6 brackets.</param>
    /// <param name="path">The request path, already URI-encoded.</param>
    /// <param name="query">The query string including its "?", already URI-encoded, or an empty string.</param>
    /// <param name="canonicalHost">The one host name that is not redirected, e.g. <c>mdreader.online</c>.</param>
    /// <param name="canonicalOrigin">The scheme and host to redirect to, with no trailing slash.</param>
    /// <returns>
    /// An absolute URL when the host is wrong, a path when only the trailing slash is, and null when the request is
    /// already canonical or is exempt.
    /// </returns>
    internal static string? RedirectTarget(string? host, string path, string query, string canonicalHost, string canonicalOrigin)
    {
        if (string.Equals(path, HealthPath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalized = NormalizePath(path);

        if (!IsExemptHost(host, canonicalHost))
        {
            // Both corrections at once, so a deep link under the old host never takes two hops.
            return canonicalOrigin + normalized + query;
        }

        // The host is right and stays right, so the Location is relative: sending someone running the app on
        // localhost to the public domain because they typed a trailing slash would be a bug, not a redirect.
        return normalized == path ? null : normalized + query;
    }

    /// <summary>Strips trailing slashes from anything but the root, which keeps its one.</summary>
    private static string NormalizePath(string path)
    {
        if (path.Length <= 1 || !path.EndsWith('/'))
        {
            return path;
        }

        var trimmed = path.TrimEnd('/');
        return trimmed.Length == 0 ? "/" : trimmed;
    }

    private static bool IsExemptHost(string? host, string canonicalHost)
    {
        if (string.IsNullOrEmpty(host) || string.Equals(host, canonicalHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // A single-label name is never a public one: "localhost", a container name, a fly machine id.
        if (!host.Contains('.', StringComparison.Ordinal) || host.EndsWith(InternalSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // An address, not a name: something reached the machine without going through DNS.
        return IPAddress.TryParse(host, out _);
    }

    /// <summary>
    /// Adds the redirect. It goes first in the pipeline, before the static files and the endpoints, so a request
    /// under the wrong URL is answered before any work is done for it.
    /// </summary>
    public static IApplicationBuilder UseCanonicalHost(this IApplicationBuilder app, SiteOptions options)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(options);

        var origin = options.NormalizedOrigin;
        var canonicalHost = new Uri(origin, UriKind.Absolute).Host;

        return app.Use(async (context, next) =>
        {
            var request = context.Request;
            var location = RedirectTarget(
                request.Host.Host.Trim('[', ']'),
                request.Path.ToUriComponent(),
                request.QueryString.ToUriComponent(),
                canonicalHost,
                origin);

            if (location is null)
            {
                await next(context);
                return;
            }

            // 301 for the reads, because that is the signal that consolidates a duplicate URL into the canonical one.
            // Anything else keeps its method and body: a 301 on a POST is allowed to arrive as a GET with nothing in
            // it, which would turn a render request into a silent and confusing failure.
            context.Response.StatusCode = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)
                ? StatusCodes.Status301MovedPermanently
                : StatusCodes.Status308PermanentRedirect;
            context.Response.Headers.Location = location;
            context.Response.Headers.CacheControl = "no-store";
        });
    }
}
