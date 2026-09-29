using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Primitives;

namespace MdReader.Web;

/// <summary>
/// Who a request is from, for rate-limiting purposes: the visitor, and separately the hop that handed the request
/// over. Nothing here is used for anything else — no address is logged or stored.
/// </summary>
/// <remarks>
/// <para>The chain is <c>visitor → Cloudflare → fly proxy → Kestrel</c>, and every step of it rewrites headers:</para>
/// <list type="bullet">
/// <item><description><c>CF-Connecting-IP</c> is set by Cloudflare to the address it accepted the connection from,
/// replacing anything the visitor sent. It is the only header that names the visitor.</description></item>
/// <item><description><c>Fly-Client-IP</c> is set by the fly proxy to the address <em>it</em> accepted from — which,
/// with Cloudflare in front, is a Cloudflare edge address shared by many unrelated visitors. Keying on it alone would
/// put everyone behind one edge in one bucket.</description></item>
/// <item><description><c>X-Forwarded-For</c> is appended to by each hop, so its <em>last</em> entry is what the
/// nearest proxy saw, and its earlier entries include anything the visitor made up.</description></item>
/// <item><description>The connection's remote address is the fly proxy itself and is the same for every visitor, so
/// it is the last resort only — which is a local run, where it really is the peer.</description></item>
/// </list>
/// <para>Hence two keys. <see cref="Get"/> is the visitor and carries the ordinary per-visitor budget.
/// <see cref="GetUpstream"/> is the hop that delivered the request, and carries a much larger budget that exists for
/// one reason: <c>CF-Connecting-IP</c> is only worth anything on a request that really came through Cloudflare, and
/// the fly origin is reachable without it. Someone who goes straight to the origin can put any value in that header
/// and mint a fresh per-visitor budget for every request — but all of those requests still share one upstream key,
/// which is their own address and which they cannot forge. Closing the hole completely means refusing requests that
/// did not come through Cloudflare, which is configuration rather than code.</para>
/// </remarks>
internal static class ClientKey
{
    /// Set by Cloudflare to the visitor's address, replacing any value the visitor sent.
    public const string CloudflareHeader = "CF-Connecting-IP";

    /// Set by the fly proxy to the address it accepted the connection from (a Cloudflare edge, when one is in front).
    public const string FlyClientIpHeader = "Fly-Client-IP";

    /// Appended to by each hop, so the last entry is the one the nearest proxy added.
    public const string ForwardedForHeader = "X-Forwarded-For";

    /// Partition of a request whose address can't be established at all.
    public const string Unknown = "unknown";

    /// <summary>The visitor: Cloudflare's view when the request came through it, otherwise the delivering hop.</summary>
    public static string Get(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Normalize(Sole(context.Request.Headers[CloudflareHeader]) ?? UpstreamAddress(context));
    }

    /// <summary>
    /// The hop that handed the request to this machine: a Cloudflare edge, or the visitor when nothing is in front.
    /// Equal to <see cref="Get"/> exactly when no proxy claimed a different visitor, which is what keeps the second
    /// budget from charging a direct visitor twice.
    /// </summary>
    public static string GetUpstream(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Normalize(UpstreamAddress(context));
    }

    private static IPAddress? UpstreamAddress(HttpContext context)
    {
        var headers = context.Request.Headers;
        return Last(headers[FlyClientIpHeader]) ?? Last(headers[ForwardedForHeader]) ?? context.Connection.RemoteIpAddress;
    }

    /// <summary>
    /// The address of a header that must carry exactly one, or null if it arrived more than once, held a list, or
    /// wasn't an address. A header a proxy overwrites has exactly one value; anything else means the proxy that would
    /// have overwritten it is not in front, so the value is the visitor's own claim and is worth nothing.
    /// </summary>
    private static IPAddress? Sole(StringValues header)
    {
        if (header.Count != 1)
        {
            return null;
        }

        var only = (header[0] ?? string.Empty).AsSpan().Trim();
        return only.Contains(',') || !TryParseAddress(only, out var address) ? null : address;
    }

    /// <summary>
    /// The last address in a header that may repeat and may hold a comma-separated list, or null if it holds none.
    /// An entry may carry a port (<c>1.2.3.4:443</c>, <c>[2001:db8::1]:443</c>); one that is not an address at all is
    /// skipped rather than discarding the whole header, and reading from the end means a value the visitor prepended
    /// can never win over the one a proxy appended.
    /// </summary>
    private static IPAddress? Last(StringValues header)
    {
        for (var index = header.Count - 1; index >= 0; index--)
        {
            var rest = (header[index] ?? string.Empty).AsSpan();
            while (!rest.IsEmpty)
            {
                var comma = rest.LastIndexOf(',');
                if (TryParseAddress(rest[(comma + 1)..].Trim(), out var address))
                {
                    return address;
                }

                if (comma < 0)
                {
                    break;
                }

                rest = rest[..comma];
            }
        }

        return null;
    }

    private static bool TryParseAddress(ReadOnlySpan<char> entry, out IPAddress? address)
    {
        if (IPAddress.TryParse(entry, out address))
        {
            return true;
        }

        // "1.2.3.4:443" and "[2001:db8::1]:443": IPEndPoint.TryParse reads both, IPAddress.TryParse neither.
        if (IPEndPoint.TryParse(entry, out var endpoint))
        {
            address = endpoint.Address;
            return true;
        }

        address = null;
        return false;
    }

    /// IPv4 (also IPv4-mapped IPv6) as is; IPv6 by its /64 prefix, which one subscriber usually owns in full.
    private static string Normalize(IPAddress? address)
    {
        if (address is null)
        {
            return Unknown;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes).ToString() + "/64";
    }
}
