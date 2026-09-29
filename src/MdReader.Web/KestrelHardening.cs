using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace MdReader.Web;

/// <summary>
/// The connection-level limits, which are the ones the application code never gets to see: a request that is refused
/// here was refused before a route was matched or a byte of body was read.
/// </summary>
/// <remarks>
/// Kestrel's defaults assume a server with room to be patient — an unbounded number of connections, and thirty
/// seconds of waiting for a request line that may never come. On one shared vCPU with 512 MB, patience is the
/// resource an attacker spends: connections are the cheapest thing to open and the slowest thing to notice.
/// </remarks>
internal static class KestrelHardening
{
    /// <summary>A request line and its headers must arrive within this, or the connection is closed.</summary>
    public static readonly TimeSpan RequestHeadersTimeout = TimeSpan.FromSeconds(15);

    /// <summary>An idle keep-alive connection is closed after this (Kestrel's default is 130 s).</summary>
    public static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(60);

    /// <summary>All headers together (Kestrel's default is 32 KB). Nothing here needs more than a few hundred bytes.</summary>
    public const int MaxRequestHeadersTotalSize = 16 * 1024;

    /// <summary>Header count (Kestrel's default is 100).</summary>
    public const int MaxRequestHeaderCount = 50;

    /// <summary>The request line (Kestrel's default is 8 KB). The longest real path here is a page name.</summary>
    public const int MaxRequestLineSize = 4 * 1024;

    public static void Apply(KestrelServerLimits kestrel, WebLimitsOptions limits)
    {
        ArgumentNullException.ThrowIfNull(kestrel);
        ArgumentNullException.ThrowIfNull(limits);

        // The render endpoint enforces the document limit itself and answers 413. Kestrel's cap is deliberately
        // higher, so that a moderately oversized upload can be drained and the client actually sees that response:
        // a connection reset mid-upload surfaces in a browser as a network error instead. Past this cap the
        // connection is closed, which is the right answer for a body nobody was going to read.
        kestrel.MaxRequestBodySize = 2L * Math.Max(1, limits.MaxRequestBodyBytes);

        kestrel.MaxConcurrentConnections = limits.MaxConcurrentConnections > 0 ? limits.MaxConcurrentConnections : null;
        kestrel.MaxConcurrentUpgradedConnections = 0;   // nothing here upgrades: no WebSockets, no HTTP/2 CONNECT
        kestrel.RequestHeadersTimeout = RequestHeadersTimeout;
        kestrel.KeepAliveTimeout = KeepAliveTimeout;
        kestrel.MaxRequestHeadersTotalSize = MaxRequestHeadersTotalSize;
        kestrel.MaxRequestHeaderCount = MaxRequestHeaderCount;
        kestrel.MaxRequestLineSize = MaxRequestLineSize;

        // Kestrel's defaults already close a connection that sends or receives slower than 240 bytes/s after a five
        // second grace; they are restated because they are the slow-request protection, and a future change to them
        // should be a decision rather than a surprise.
        kestrel.MinRequestBodyDataRate = new MinDataRate(bytesPerSecond: 240, gracePeriod: TimeSpan.FromSeconds(5));
        kestrel.MinResponseDataRate = new MinDataRate(bytesPerSecond: 240, gracePeriod: TimeSpan.FromSeconds(5));
    }
}
