namespace MdReader.Core.Paths;

/// <summary>
/// The two rules every http(s) URL in MdReader has to pass, wherever it came from: it may not carry credentials, and
/// it may not name one of the app's own virtual hosts. One implementation, shared by <see cref="LinkClassifier"/>,
/// the sanitizer's URL rewriter and <c>MdReader.Core.Net.DocumentUrl</c>, so the three can't drift apart.
/// </summary>
public static class WebUrlPolicy
{
    /// <summary>The app's own virtual hosts (<c>app.</c> and <c>doc.</c>) live under this domain.</summary>
    public const string ReservedDomain = "mdreader.example";

    /// <summary>
    /// <see cref="ReservedDomain"/> or any subdomain of it, checked on both the host and its IDNA (ASCII) form and
    /// ignoring a trailing dot, so neither a Unicode homograph nor <c>doc.mdreader.example.</c> slips past.
    /// </summary>
    public static bool IsReservedHost(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return IsReservedHostName(uri.Host) || IsReservedHostName(uri.IdnHost);
    }

    /// <summary>The host-name half of <see cref="IsReservedHost"/>.</summary>
    public static bool IsReservedHostName(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        var name = host.AsSpan().TrimEnd('.');
        return name.Equals(ReservedDomain, StringComparison.OrdinalIgnoreCase)
               || (name.Length > ReservedDomain.Length
                   && name.EndsWith(ReservedDomain, StringComparison.OrdinalIgnoreCase)
                   && name[^(ReservedDomain.Length + 1)] == '.');
    }

    /// <summary>
    /// Credentials in a URL are a phishing vector ("https://github.com@evil.example/") and, over HTTP, a way to make
    /// the app send a password somewhere. Checked twice: on the parsed <see cref="Uri.UserInfo"/>, and independently
    /// on the authority of the string the author wrote, which also catches "https://@host".
    /// </summary>
    public static bool HasCredentials(Uri uri, string? original = null)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.UserInfo.Length > 0 || AuthorityContainsAt(original ?? uri.OriginalString);
    }

    /// <summary>True if the authority part ("scheme://authority/…") of the original string contains '@'.</summary>
    private static bool AuthorityContainsAt(string original)
    {
        var colon = original.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return false;
        }

        var rest = original.AsSpan(colon + 1).TrimStart("/\\");
        var end = rest.IndexOfAny("/\\?#");
        return (end < 0 ? rest : rest[..end]).Contains('@');
    }
}
