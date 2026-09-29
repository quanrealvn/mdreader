namespace MdReader.Web;

/// <summary>
/// Settings for the public pages (ARCHITECTURE §15). Bound from the <c>Site</c> configuration section, so a test — or
/// a second deployment — can point the canonical URLs somewhere else without a rebuild.
/// </summary>
internal sealed class SiteOptions
{
    public const string SectionName = "Site";

    /// <summary>
    /// The origin every canonical, Open Graph and sitemap URL is written against, with no trailing slash. It is the
    /// apex and not the request's own host on purpose: the app answers on <c>mdreader.fly.dev</c> too, and both copies
    /// must point search engines at the same one.
    /// </summary>
    public string Origin { get; set; } = "https://mdreader.online";

    /// <summary>
    /// The token from Search Console's "HTML tag" verification method, without the surrounding markup. Empty (the
    /// default) leaves the tag out entirely; a DNS TXT record verifies the domain without needing this at all.
    /// </summary>
    public string GoogleSiteVerification { get; set; } = string.Empty;

    /// <summary>The origin with any trailing slash removed, which is how every URL here is built.</summary>
    public string NormalizedOrigin => Origin.TrimEnd('/');

    /// <summary>The absolute URL of a site path ("/" or "/markdown-to-pdf").</summary>
    public string Absolute(string path) => path == "/" ? NormalizedOrigin + "/" : NormalizedOrigin + path;
}
