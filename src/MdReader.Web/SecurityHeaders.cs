namespace MdReader.Web;

/// <summary>Response headers sent on every response (ARCHITECTURE §15).</summary>
internal static class SecurityHeaders
{
    /// <summary>
    /// <c>manifest-src 'self'</c> is not decoration: <c>manifest-src</c> falls back only to <c>default-src</c>, which
    /// is <c>'none'</c> here, so without it the browser refuses the web app manifest and the app can't be installed.
    /// <c>worker-src 'self'</c> is what the service worker already resolves to through the <c>child-src</c> →
    /// <c>script-src</c> fallback, written out so that tightening <c>script-src</c> later can't take the worker with
    /// it unnoticed. Neither loosens anything: both name the origin the page itself came from.
    /// <para><c>media-src</c>, <c>object-src</c> and <c>frame-src</c> are already denied by <c>default-src 'none'</c>.
    /// They are written out because that is what a reader (and a scanner) checks, and because relaxing
    /// <c>default-src</c> later must not quietly bring plugins, media and frames back with it. <c>child-src</c> is
    /// deliberately left out: it is the fallback <c>worker-src</c> overrides, and naming it <c>'none'</c> next to
    /// <c>worker-src 'self'</c> would read like a contradiction.</para>
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' https: data:; " +
        "font-src 'self' data:; connect-src 'self'; manifest-src 'self'; worker-src 'self'; media-src 'none'; " +
        "object-src 'none'; frame-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    /// Every powerful feature off. clipboard-write is deliberately absent (default: this origin only): the code-block
    /// Copy button uses the Clipboard API.
    public const string PermissionsPolicy =
        "accelerometer=(), autoplay=(), camera=(), display-capture=(), encrypted-media=(), fullscreen=(), " +
        "geolocation=(), gyroscope=(), hid=(), idle-detection=(), magnetometer=(), microphone=(), midi=(), " +
        "payment=(), picture-in-picture=(), publickey-credentials-get=(), screen-wake-lock=(), serial=(), usb=(), " +
        "xr-spatial-tracking=()";

    /// <summary>
    /// fly.io terminates TLS and redirects http → https (force_https); browsers ignore HSTS on plain-http responses.
    /// <c>includeSubDomains</c> suits a site that serves one apex name and has no plain-http subdomain to protect.
    /// <c>preload</c> is deliberately not claimed: it submits the domain to a list baked into browsers and is slow to
    /// undo, so it is the owner's decision rather than a default.
    /// </summary>
    public const string StrictTransportSecurity = "max-age=31536000; includeSubDomains";

    public static void Apply(IHeaderDictionary headers)
    {
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = PermissionsPolicy;
        headers.StrictTransportSecurity = StrictTransportSecurity;

        // frame-ancestors already covers every browser that reads CSP; this covers the ones that don't.
        headers.XFrameOptions = "DENY";

        // No cross-origin window keeps a handle on this one, and no other site may load our responses as a
        // subresource. Neither costs anything here: the page opens links with noopener and loads only its own assets.
        headers["Cross-Origin-Opener-Policy"] = "same-origin";
        headers["Cross-Origin-Resource-Policy"] = "same-origin";
    }

    /// Adds the headers when the response starts, so they are also on responses written by other middleware
    /// (static files, 304s, rate-limiter rejections, 404s).
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            context.Response.OnStarting(static state =>
            {
                Apply(((HttpResponse)state).Headers);
                return Task.CompletedTask;
            }, context.Response);
            return next(context);
        });
}
