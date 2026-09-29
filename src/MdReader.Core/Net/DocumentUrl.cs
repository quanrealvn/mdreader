using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using MdReader.Core.Paths;

namespace MdReader.Core.Net;

/// <summary>
/// The single gate every http(s) address passes through before MdReader does anything with it: a command-line
/// argument, text on the clipboard, a drop, a request forwarded by another instance, a link click, and every hop of
/// a redirect chain (ARCHITECTURE §8). Purely lexical — it never opens a connection.
/// </summary>
/// <remarks>
/// The rules are deliberately narrow, because everything downstream trusts them: absolute http or https, a host, no
/// credentials, not one of the app's own virtual hosts, and short enough to log, forward over the pipe and put in a
/// tab's tooltip. The error text is a finished sentence: callers show it in the status toast as it is.
/// </remarks>
public static class DocumentUrl
{
    /// <summary>Longest address accepted. Well past every real raw-content URL, well short of a denial of service.</summary>
    public const int MaxLength = 2048;

    private const string NotAWebAddress = "That isn't a web address (it has to start with http:// or https://).";

    /// <summary>
    /// A cheap prefix test: does this string want to be an http(s) URL? Used where a string may be either a file path
    /// or an address (the command line, a drop, the single-instance pipe). A true answer still has to pass
    /// <see cref="TryParse"/>; a false answer is treated as a path.
    /// </summary>
    public static bool LooksLikeUrl([NotNullWhen(true)] string? text)
    {
        if (text is null)
        {
            return false;
        }

        var trimmed = text.AsSpan().Trim();
        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Parses and validates an address. On failure <paramref name="error"/> is a sentence for the user.
    /// </summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out Uri? url, [NotNullWhen(false)] out string? error)
    {
        url = null;
        var trimmed = text?.Trim() ?? string.Empty;

        // The same rule LooksLikeUrl applies, so the two can't disagree: LooksLikeUrl decides whether a string is
        // routed here at all, and a string it calls a path must not turn out to be an address after all. Uri is more
        // forgiving than that - it reads "http:/\example.com/a.md" as a perfectly good URL - and the gate a caller
        // consults must be the gate that answers.
        if (!LooksLikeUrl(trimmed))
        {
            error = NotAWebAddress;
            return false;
        }

        if (trimmed.Length > MaxLength)
        {
            error = $"That web address is too long (over {MaxLength} characters).";
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var candidate))
        {
            error = NotAWebAddress;
            return false;
        }

        if (!IsAllowed(candidate, trimmed, out error))
        {
            return false;
        }

        url = candidate;
        error = null;
        return true;
    }

    /// <summary>
    /// The same rules applied to an already-parsed URI, for a redirect target. <paramref name="original"/> is the
    /// string the URI came from when there is one (the credential check looks at it as well).
    /// </summary>
    public static bool IsAllowed(Uri url, string? original, [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(url);

        if (!url.IsAbsoluteUri
            || !(url.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                 || url.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            error = NotAWebAddress;
            return false;
        }

        if (url.Host.Length == 0)
        {
            error = "That web address has no server name.";
            return false;
        }

        if (WebUrlPolicy.HasCredentials(url, original))
        {
            // "https://github.com@evil.example/README.md" reads like GitHub and isn't; over http the password would
            // also go out on the wire. MdReader never signs in to a server, so there is nothing to keep here.
            error = "MdReader doesn't open web addresses that contain a user name or password.";
            return false;
        }

        if (WebUrlPolicy.IsReservedHost(url))
        {
            error = "That address belongs to MdReader's own viewer and can't be opened.";
            return false;
        }

        if (url.AbsoluteUri.Length > MaxLength)
        {
            error = $"That web address is too long (over {MaxLength} characters).";
            return false;
        }

        error = null;
        return true;
    }

    /// <inheritdoc cref="IsAllowed(Uri, string?, out string?)"/>
    public static bool IsAllowed(Uri url, [NotNullWhen(false)] out string? error) => IsAllowed(url, null, out error);

    /// <summary>
    /// What a tab for this address is called: the last non-empty path segment (percent-decoded), else the host. Never
    /// empty, never longer than 120 characters, and never a path the file system could be asked about.
    /// </summary>
    public static string DisplayName(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        var path = url.AbsolutePath;
        var end = path.Length;
        while (end > 0 && path[end - 1] == '/')
        {
            end--;
        }

        var start = end > 0 ? path.LastIndexOf('/', end - 1) + 1 : 0;
        var segment = end > start ? path[start..end] : string.Empty;
        if (segment.Contains('%', StringComparison.Ordinal))
        {
            // Uri.UnescapeDataString leaves invalid sequences alone rather than throwing (see ReferenceParser).
            segment = Uri.UnescapeDataString(segment);
        }

        var name = segment.Length > 0 ? segment : url.Host;

        // Control characters would break the tab strip's layout and the log, and a very long segment would take the
        // whole tab strip. Both are the server's choice, so neither is trusted to be sensible.
        var cleaned = SanitizeDisplayText(name);
        if (cleaned.Length == 0)
        {
            cleaned = SanitizeDisplayText(url.Host);
        }

        if (cleaned.Length == 0)
        {
            return "document";
        }

        return cleaned.Length <= 120 ? cleaned : string.Concat(cleaned.AsSpan(0, 119), "…");
    }

    /// <summary>
    /// Text a server chose, made safe to put in front of someone: control characters and Unicode format characters
    /// become spaces, and the result is trimmed.
    /// </summary>
    /// <remarks>
    /// The format characters matter as much as the controls. U+202E RIGHT-TO-LEFT OVERRIDE turns
    /// <c>…/%E2%80%AEgnp.exe</c> into a tab that reads "exe.png": the name is a lie told by the renderer, not by the
    /// text. The same string is offered as a PDF file name, and RLO is not in
    /// <see cref="System.IO.Path.GetInvalidFileNameChars"/>, so it has to be stopped here.
    /// </remarks>
    public static string SanitizeDisplayText(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0)
        {
            return string.Empty;
        }

        var buffer = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            buffer[i] = char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format ? ' ' : c;
        }

        return new string(buffer).Trim();
    }
}
