using MdReader.Core.Protocol;

namespace MdReader.Shell.Documents;

/// <summary>The viewer page's identity (§7.1 rule 7). Pure URL arithmetic: no web-view type is involved.</summary>
/// <remarks>
/// The scheme is read from <see cref="ProtocolConstants.WebScheme"/> instead of being written out, because macOS
/// serves the same two hosts over a scheme of its own; the host, the port and the path are the rule everywhere.
/// </remarks>
public static class PageOrigin
{
    /// True only for the app host's /index.html with any query (and any fragment).
    public static bool IsPageUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || !IsAppOrigin(uri))
        {
            return false;
        }

        return string.Equals(uri.AbsolutePath, "/index.html", StringComparison.Ordinal);
    }

    /// §7.1 rule 7: compare scheme + host of <c>new Uri(source)</c>, never the raw string.
    public static bool IsAppOrigin(string? source) =>
        Uri.TryCreate(source, UriKind.Absolute, out Uri? uri) && IsAppOrigin(uri);

    private static bool IsAppOrigin(Uri uri) =>
        string.Equals(uri.Scheme, ProtocolConstants.WebScheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, ProtocolConstants.AppHost, StringComparison.OrdinalIgnoreCase)
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo);
}
