namespace MdReader.Web;

/// <summary>
/// Abuse limits of the web version (ARCHITECTURE §15). Bound from the "Limits" configuration section, so a deployment
/// (env <c>Limits__RendersPerWindow=120</c>) or a test can change them.
/// </summary>
/// <remarks>
/// <para>The defaults are sized for the deployed machine: one shared vCPU and 512 MB (fly.toml). The measurements
/// behind them were taken against one pinned core of a Ryzen 7 7800X3D, with <c>DOTNET_PROCESSOR_COUNT=1</c> and the
/// 384 MB heap limit a 512 MB container gives .NET.</para>
/// <para>What the measurement found, and what most of the numbers here follow from: ordinary prose renders at about
/// half a second per megabyte, but a document of the same size can cost several seconds and several hundred
/// megabytes if it is shaped for it — a megabyte of bare URLs becomes a million link nodes, a megabyte of dense
/// table rows becomes a hundred and fifty thousand cells. At 2 MB, and at 1 MB, several such shapes exhausted the
/// heap and died with OutOfMemoryException after burning the CPU to get there. At 512 KB every shape tried renders,
/// the worst in ~1.2 s, and eight at once peaked at 477 MB of working set inside the container's 512.</para>
/// <para>So the accepted size is what came down, from 2 MB to 512 KB. It is a property of the machine: a deployment
/// with a gigabyte can raise it (<c>Limits__MaxRequestBodyBytes</c>) and should re-run those measurements first.</para>
/// </remarks>
public sealed class WebLimitsOptions
{
    public const string SectionName = "Limits";

    /// <summary>
    /// The default for <see cref="MaxRequestBodyBytes"/>. The front ends carry the same number so they can refuse an
    /// oversized document without a round trip, and <c>RenderApiTests</c> checks that the three agree.
    /// </summary>
    public const int DefaultMaxRequestBodyBytes = 512 * 1024;

    /// <summary>
    /// Largest accepted <c>POST /api/render</c> body; larger → 413. Configurable because it is a property of the
    /// machine rather than of the format: see the remarks above for why this one is 512 KB.
    /// </summary>
    public int MaxRequestBodyBytes { get; set; } = DefaultMaxRequestBodyBytes;

    /// Renders one client may request per <see cref="RateWindow"/> (fixed window); more → 429 with Retry-After.
    public int RendersPerWindow { get; set; } = 60;

    /// <summary>
    /// Document bytes one client may send per <see cref="RateWindow"/>; more → 429 with Retry-After. A request count
    /// on its own does not bound cost: 30 renders of the worst 2 MB document measured 107 s of CPU, which is more
    /// than a one-vCPU machine has in the minute those 30 requests are allowed, so one address could hold the site
    /// down without ever reaching its request limit. Render cost grows with document size, so charging bytes bounds
    /// it. Four megabytes a minute is four full-size documents, or hundreds of the small re-renders the split-view
    /// editor sends while someone types.
    /// </summary>
    public int RenderBytesPerWindow { get; set; } = 4 * 1024 * 1024;

    /// <summary>
    /// Document bytes one <em>upstream hop</em> may send per <see cref="RateWindow"/>. Behind Cloudflare that hop is
    /// an edge address shared by many unrelated visitors, so this has to be far larger than the per-visitor budget.
    /// It exists so that someone who reaches the fly origin directly and invents a <c>CF-Connecting-IP</c> for every
    /// request still meets a ceiling they cannot forge their way past (<see cref="ClientKey"/>). Eight times the
    /// per-visitor budget is 32 MB a minute — already more CPU than this machine has — so it is a backstop against
    /// forgery, not a second rate limit that ordinary traffic should ever feel.
    /// </summary>
    public int UpstreamBytesPerWindow { get; set; } = 32 * 1024 * 1024;

    public TimeSpan RateWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Renders running at the same time, server-wide. One per core, not two: a second render on a one-vCPU machine
    /// buys no throughput and doubles the peak memory, and memory is what this machine runs out of first. Measured
    /// with two at once, a 1 MB document of repeated headings exhausted the heap; one at a time, every shape tried
    /// at that size rendered.
    /// </summary>
    public int MaxConcurrentRenders { get; set; } = Environment.ProcessorCount;

    /// Renders waiting for a free slot; when the queue is full as well → 503.
    public int RenderQueueLength { get; set; } = 8;

    /// <summary>
    /// Wall clock one render may take before it is abandoned (→ 422). Core observes the token inside the two stages
    /// that are otherwise a single uncancellable call — Markdig writing its HTML and AngleSharp building its tree —
    /// so the thread is really given back instead of running on behind an answered request. Eight seconds is about
    /// thirty times a 1 MB prose render, which leaves a slow shared vCPU plenty of room.
    /// </summary>
    public TimeSpan RenderBudget { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Wall clock a whole <c>/api/render</c> request may take, its upload included (→ 408). Bounds a client that
    /// dribbles its body out just above Kestrel's minimum data rate, which on its own takes hours.
    /// </summary>
    public TimeSpan RequestBudget { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// HTML elements one render may build. Core's size-derived budget is sized for a desktop, where a DOM of a few
    /// hundred megabytes is merely rude; here it is fatal. This does not replace the size cap above, and cannot —
    /// by the time the DOM is being built, the memory has often already gone into Markdig's syntax tree. What it
    /// stops is the amplification class, where a few kilobytes of raw HTML expand into millions of cloned elements
    /// and no size cap would ever notice. A hundred thousand is just above what the size cap can produce honestly
    /// (a 512 KB document of the densest legitimate shape measured builds about eighty thousand), so a document that
    /// wants more has amplified, and it is shown as plain text rather than failing.
    /// </summary>
    public int MaxRenderElements { get; set; } = 100_000;

    /// Connections Kestrel holds open at once; beyond it, new connections wait for one to close.
    public int MaxConcurrentConnections { get; set; } = 512;
}
