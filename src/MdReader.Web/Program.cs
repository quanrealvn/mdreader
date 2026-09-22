using System.IO.Compression;
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

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.AddServerHeader = false;
    // The render endpoint enforces the 2 MB limit itself (413). Kestrel's hard cap is higher so that it can drain a
    // moderately oversized upload after the 413: a browser only sees the response if its upload wasn't cut off (a
    // reset connection surfaces as a network error instead). Anything beyond this cap gets the connection closed.
    kestrel.Limits.MaxRequestBodySize = 2L * WebLimitsOptions.MaxRequestBodyBytes;
});

builder.Services.Configure<WebLimitsOptions>(builder.Configuration.GetSection(WebLimitsOptions.SectionName));
builder.Services.AddSingleton<IMarkdownRenderer>(_ => new MarkdownRenderer(PhysicalFileSystemProbe.Instance));
builder.Services.AddSingleton<RenderGate>();

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

app.UseSecurityHeaders();
app.UseResponseCompression();
app.UseRateLimiter();

var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".md"] = "text/markdown; charset=utf-8";
contentTypes.Mappings[".js"] = "text/javascript; charset=utf-8";

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypes,
    OnPrepareResponse = context =>
    {
        // File names carry no content hash: third-party files (changed only with a deploy that bumps them) are cached
        // for a day; the app's own files are revalidated on every load (ETag → 304), so a deploy is picked up at once.
        context.Context.Response.Headers.CacheControl = context.Context.Request.Path.StartsWithSegments("/vendor")
            ? "public, max-age=86400"
            : "no-cache";
    },
});

app.MapRenderEndpoint();
app.MapGet("/healthz", (HttpContext context) =>
{
    context.Response.Headers.CacheControl = "no-store";
    return Results.Text("ok");
});

app.Run();

/// <summary>Entry point; public for <c>WebApplicationFactory&lt;Program&gt;</c> in the tests.</summary>
public partial class Program;
