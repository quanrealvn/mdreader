using System.Net;
using System.Net.Sockets;

namespace MdReader.Web;

/// <summary>The rate-limit partition of a request: the visitor's IP address.</summary>
internal static class ClientKey
{
    /// Set by the fly.io proxy to the address it accepted the connection from (the app is only reachable through it).
    public const string FlyClientIpHeader = "Fly-Client-IP";

    public static string Get(HttpContext context)
    {
        var address = IPAddress.TryParse(context.Request.Headers[FlyClientIpHeader].ToString().Trim(), out var fromProxy)
            ? fromProxy
            : context.Connection.RemoteIpAddress;
        return address is null ? "unknown" : Normalize(address);
    }

    /// IPv4 (also IPv4-mapped IPv6) as is; IPv6 by its /64 prefix, which one subscriber usually owns in full.
    private static string Normalize(IPAddress address)
    {
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
