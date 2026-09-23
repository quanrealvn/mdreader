namespace MdReader.Core.Protocol;

public static class ProtocolConstants
{
    public const int Version = 1;
    public const string AppHost = "app.mdreader.example";
    public const string DocHost = "doc.mdreader.example";
    public const int MaxPartChars = 1_000_000;
    public const int MaxIncomingMessageChars = 8_000_000;

    /// The scheme the two virtual hosts are served over on Windows, where WebView2's virtual host mapping keeps it.
    public const string HttpsScheme = "https";

    /// <summary>
    /// The scheme the same two hosts are served over on macOS. <c>WKURLSchemeHandler</c> refuses every scheme WebKit
    /// handles itself, https included, so a scheme of our own is the only way to serve the app folder and the document
    /// folder from inside the process. The host names don't change, so every path rule, link rule and log line reads
    /// the same on both platforms; only the three URLs below differ.
    /// </summary>
    public const string MacScheme = "mdreader";

    /// The scheme in force for this process; see <see cref="UseScheme"/>.
    public static string WebScheme { get; private set; } = HttpsScheme;

    public static string AppOrigin { get; private set; } = HttpsScheme + "://" + AppHost;

    public static string DocBaseUrl { get; private set; } = HttpsScheme + "://" + DocHost + "/";

    /// The viewer page; the shell appends <c>?theme=light|dark</c>.
    public static string PageUrl { get; private set; } = HttpsScheme + "://" + AppHost + "/index.html";

    /// <summary>
    /// Switches the process to another scheme. The macOS shell calls it once at startup, before anything is rendered,
    /// because its web view cannot serve https; nothing else calls it, and on Windows the default stands. Repeating
    /// the same scheme is a no-op, so a second call from a test fixture is harmless.
    /// </summary>
    public static void UseScheme(string scheme)
    {
        ArgumentException.ThrowIfNullOrEmpty(scheme);
        if (string.Equals(scheme, WebScheme, StringComparison.Ordinal))
        {
            return;
        }

        if (!IsValidScheme(scheme))
        {
            throw new ArgumentException($"'{scheme}' isn't a URL scheme.", nameof(scheme));
        }

        WebScheme = scheme;
        AppOrigin = scheme + "://" + AppHost;
        DocBaseUrl = scheme + "://" + DocHost + "/";
        PageUrl = AppOrigin + "/index.html";
    }

    /// An RFC 3986 scheme: a letter, then letters, digits, '+', '-' or '.'.
    private static bool IsValidScheme(string scheme)
    {
        if (!char.IsAsciiLetter(scheme[0]))
        {
            return false;
        }

        foreach (char c in scheme.AsSpan(1))
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }
}
