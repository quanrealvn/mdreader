namespace MdReader.Web;

/// <summary>Response headers sent on every response (ARCHITECTURE §15).</summary>
internal static class SecurityHeaders
{
    public const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' https: data:; " +
        "font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";

    /// Every powerful feature off. clipboard-write is deliberately absent (default: this origin only): the code-block
    /// Copy button uses the Clipboard API.
    public const string PermissionsPolicy =
        "accelerometer=(), autoplay=(), camera=(), display-capture=(), encrypted-media=(), fullscreen=(), " +
        "geolocation=(), gyroscope=(), hid=(), idle-detection=(), magnetometer=(), microphone=(), midi=(), " +
        "payment=(), picture-in-picture=(), publickey-credentials-get=(), screen-wake-lock=(), serial=(), usb=(), " +
        "xr-spatial-tracking=()";

    /// fly.io terminates TLS and redirects http → https (force_https); browsers ignore HSTS on plain-http responses.
    public const string StrictTransportSecurity = "max-age=31536000";

    public static void Apply(IHeaderDictionary headers)
    {
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = PermissionsPolicy;
        headers.StrictTransportSecurity = StrictTransportSecurity;
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
