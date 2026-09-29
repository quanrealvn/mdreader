using System.IO.Compression;
using System.Text;
using System.Threading.RateLimiting;
using MdReader.Core.Paths;
using MdReader.Core.Rendering;
using MdReader.Web;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;

// MdReader web version (ARCHITECTURE §15): the page from wwwroot plus POST /api/render, which renders Markdown with the
// desktop app's renderer and sanitizer. fly.io terminates TLS in front of Kestrel (plain http on ASPNETCORE_HTTP_PORTS).
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // wwwroot is composed at build time into the output folder (see the csproj), wherever the content root is.
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
});

// Framework chatter (routing, static files, request start/finish) only at Warning; the render endpoint logs one line
// per request with sizes, timings and status — never document content.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

var limitsSection = builder.Configuration.GetSection(WebLimitsOptions.SectionName);
var startupLimits = limitsSection.Get<WebLimitsOptions>() ?? new WebLimitsOptions();

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    KestrelHardening.Apply(kestrel.Limits, startupLimits);
});

builder.Services.Configure<WebLimitsOptions>(limitsSection);
// DocumentRenderer, not MarkdownRenderer: it picks the renderer from the document name, which is how a request
// asking for "json" gets JSON. The public pages go through the same instance and are named .md, so they are
// unaffected — the decision is per document, not per server.
builder.Services.AddSingleton<IMarkdownRenderer>(_ => new DocumentRenderer(PhysicalFileSystemProbe.Instance));
builder.Services.AddSingleton<RenderGate>();
builder.Services.AddSingleton<RenderCostLimiter>();
builder.Services.AddSingleton<ServiceWorkerScript>();

// The public pages (ARCHITECTURE §15.1). Bound through IOptions like the limits above, so configuration applied
// late — by a test — still takes effect; the canonical-host middleware then resolves it once, after the host is
// built and the configuration is complete.
builder.Services.Configure<SiteOptions>(builder.Configuration.GetSection(SiteOptions.SectionName));
builder.Services.AddSingleton(services => services.GetRequiredService<IOptions<SiteOptions>>().Value);
builder.Services.AddSingleton<SiteContent>();
builder.Services.AddSingleton<SiteDocuments>();

builder.Services.AddRateLimiter(options =>
{
    options.OnRejected = RenderEndpoint.OnRateLimitedAsync;
    options.AddPolicy(RenderEndpoint.RateLimitPolicy, context =>
    {
        // Read per partition (not captured at startup) so configuration applied late — tests — still takes effect.
        var limits = context.RequestServices.GetRequiredService<IOptions<WebLimitsOptions>>().Value;
        return RateLimitPartition.GetFixedWindowLimiter(ClientKey.Get(context), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, limits.RendersPerWindow),
            Window = limits.RateWindow > TimeSpan.Zero ? limits.RateWindow : TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });
});

builder.Services.AddResponseCompression(options =>
{
    // No secrets in any response (no cookies, no auth), so compressing reflected input over HTTPS is safe.
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["text/javascript", "image/svg+xml", "text/markdown"]);
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);

var app = builder.Build();

// The security headers go on first so that they are also on the redirect below, which is a response like any other.
// Then the canonical URL: a request under the wrong host name, or with a trailing slash, is answered before any work
// is done for it. The health check, localhost and anything that reached the machine directly are exempt — see
// CanonicalHost.
app.UseSecurityHeaders();
app.UseCanonicalHost(app.Services.GetRequiredService<SiteOptions>());
app.UseResponseCompression();
app.UseRateLimiter();

var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".md"] = "text/markdown; charset=utf-8";
contentTypes.Mappings[".js"] = "text/javascript; charset=utf-8";
// Not in every framework version's default table, and a manifest served as anything else is ignored by the browser.
contentTypes.Mappings[".webmanifest"] = "application/manifest+json";

// "/" is still wwwroot/index.html, handed over by the static-file middleware before routing is reached. The public
// pages are additions under their own paths (SiteCatalog); none of them is "/", and nothing here rewrites it.
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypes,
    OnPrepareResponse = context =>
    {
        // No file name here carries a content hash, so caching is a trade against how fast a deploy is picked up.
        //
        // Cached hard: the vendored libraries (mermaid alone is most of a page load's bytes) and the pictures on the
        // public pages. Both only change when a deploy replaces them, and a browser holding last week's copy of
        // either still renders a correct page — so they are fresh for a week and may then be served stale for
        // another week while the browser revalidates in the background. A returning visitor fetches nothing.
        //
        // Revalidated every load: the reader's and the pages' own HTML, CSS and JavaScript. Their documents are
        // served no-cache, so a stale module beside a fresh page would be a version mismatch, and a wrong page is
        // worse than a conditional request that costs a 304 and a couple of hundred bytes.
        var path = context.Context.Request.Path;
        var longLived = path.StartsWithSegments("/vendor")
            || path.StartsWithSegments(SiteAssets.Prefix.TrimEnd('/') + "/img");
        context.Context.Response.Headers.CacheControl = longLived
            ? "public, max-age=604800, stale-while-revalidate=604800"
            : "no-cache";
    },
});

app.MapRenderEndpoint();
app.MapSite();

// The service worker is generated, not served from wwwroot (the csproj keeps the template out of it): its cache
// version is a hash of the reader's own files, so /sw.js changes exactly when the reader changes (ARCHITECTURE §15).
app.MapGet(ServiceWorkerScript.RequestPath, (HttpContext context, ServiceWorkerScript worker) =>
{
    // Browsers bypass the HTTP cache for a worker script anyway; saying so keeps a proxy from getting ideas.
    context.Response.Headers.CacheControl = "no-cache";
    return Results.Text(worker.Content.Script, "text/javascript", Encoding.UTF8);
});

app.MapGet(CanonicalHost.HealthPath, (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Text("ok");
});

app.Run();

/// <summary>Entry point; public for <c>WebApplicationFactory&lt;Program&gt;</c> in the tests.</summary>
public partial class Program;
