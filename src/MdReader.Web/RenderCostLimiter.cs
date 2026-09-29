using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace MdReader.Web;

/// <summary>
/// Budgets of document <em>bytes</em> per window, charged before a document is read or rendered. This runs beside the
/// per-visitor request counter, which only bounds how often someone may ask; this bounds how much work they may ask
/// for, which is what actually costs the machine.
/// </summary>
/// <remarks>
/// <para>Two budgets, for the reason <see cref="ClientKey"/> explains. The per-visitor one
/// (<see cref="WebLimitsOptions.RenderBytesPerWindow"/>) is keyed on the address Cloudflare reports, which a request
/// that skipped Cloudflare can invent. The per-upstream one (<see cref="WebLimitsOptions.UpstreamBytesPerWindow"/>)
/// is keyed on the address the fly proxy saw, which nobody can invent, and is large enough that only an attacker or a
/// genuinely busy Cloudflare edge reaches it. It is charged only when the two keys differ, so a visitor with nothing
/// in front of them pays once.</para>
/// <para>The charge is the declared <c>Content-Length</c>, so an expensive request is turned away before its body is
/// read. A request that declares no length (chunked) is charged the full body cap: the server cannot know what is
/// coming, and every browser sends a length for a JSON body.</para>
/// </remarks>
internal sealed class RenderCostLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<HttpContext> _perVisitor;
    private readonly PartitionedRateLimiter<HttpContext> _perUpstream;

    public RenderCostLimiter()
    {
        _perVisitor = Create(ClientKey.Get, limits => limits.RenderBytesPerWindow);
        _perUpstream = Create(ClientKey.GetUpstream, limits => limits.UpstreamBytesPerWindow);
    }

    /// <summary>
    /// Charges <paramref name="bytes"/> to both budgets. The returned lease is acquired only if both were; dispose it
    /// either way, and read <c>IsAcquired</c> and the retry metadata from it.
    /// </summary>
    public async ValueTask<RateLimitLease> AcquireAsync(HttpContext context, long bytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var limits = Limits(context);

        var visitor = await _perVisitor.AcquireAsync(context, Charge(bytes, limits.RenderBytesPerWindow), cancellationToken);
        if (!visitor.IsAcquired
            || string.Equals(ClientKey.Get(context), ClientKey.GetUpstream(context), StringComparison.Ordinal))
        {
            return visitor;
        }

        // The visitor's permits are not handed back if the upstream budget then refuses: a fixed window has no
        // refund, and the case this exists for is one client wearing many visitor keys, where charging them all is
        // exactly right.
        var upstream = await _perUpstream.AcquireAsync(context, Charge(bytes, limits.UpstreamBytesPerWindow), cancellationToken);
        visitor.Dispose();
        return upstream;
    }

    public void Dispose()
    {
        _perVisitor.Dispose();
        _perUpstream.Dispose();
    }

    private static PartitionedRateLimiter<HttpContext> Create(Func<HttpContext, string> key, Func<WebLimitsOptions, int> budget) =>
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
        {
            // Read per partition, not captured once, so configuration applied late — by a test — still takes effect.
            var limits = Limits(context);
            return RateLimitPartition.GetFixedWindowLimiter(key(context), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = Math.Max(1, budget(limits)),
                Window = limits.RateWindow > TimeSpan.Zero ? limits.RateWindow : TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true,
            });
        });

    private static WebLimitsOptions Limits(HttpContext context) =>
        context.RequestServices.GetRequiredService<IOptions<WebLimitsOptions>>().Value;

    /// A single request may cost a whole window but never more, or it could never be served at all.
    private static int Charge(long bytes, int budget) => (int)Math.Clamp(bytes, 1, Math.Max(1, budget));
}
