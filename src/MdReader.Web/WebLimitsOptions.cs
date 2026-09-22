namespace MdReader.Web;

/// <summary>
/// Abuse limits of the web version (ARCHITECTURE §15). Bound from the "Limits" configuration section, so a deployment
/// (env <c>Limits__RendersPerWindow=120</c>) or a test can change them. The request body cap is fixed.
/// </summary>
public sealed class WebLimitsOptions
{
    public const string SectionName = "Limits";

    /// Largest accepted <c>POST /api/render</c> body; larger → 413.
    public const int MaxRequestBodyBytes = 2 * 1024 * 1024;

    /// Renders one client may request per <see cref="RateWindow"/> (fixed window); more → 429 with Retry-After.
    public int RendersPerWindow { get; set; } = 60;

    public TimeSpan RateWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// Renders running at the same time, server-wide.
    public int MaxConcurrentRenders { get; set; } = 2 * Environment.ProcessorCount;

    /// Renders waiting for a free slot; when the queue is full as well → 503.
    public int RenderQueueLength { get; set; } = 8;
}
