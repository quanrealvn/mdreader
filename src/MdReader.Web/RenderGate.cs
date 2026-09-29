using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace MdReader.Web;

/// <summary>Server-wide render concurrency limit with a short queue (ARCHITECTURE §15); no permit → 503.</summary>
internal sealed class RenderGate : IDisposable
{
    private readonly ConcurrencyLimiter _limiter;

    public RenderGate(IOptions<WebLimitsOptions> options)
    {
        var limits = options.Value;
        _limiter = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = Math.Max(1, limits.MaxConcurrentRenders),
            QueueLimit = Math.Max(0, limits.RenderQueueLength),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        });
    }

    public ValueTask<RateLimitLease> AcquireAsync(CancellationToken cancellationToken) =>
        _limiter.AcquireAsync(1, cancellationToken);

    public void Dispose() => _limiter.Dispose();
}
