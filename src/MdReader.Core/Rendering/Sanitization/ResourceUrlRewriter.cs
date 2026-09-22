using System.Globalization;
using MdReader.Core.Paths;
using MdReader.Core.Protocol;

namespace MdReader.Core.Rendering.Sanitization;

/// <summary>
/// URL rules of the sanitizer (ARCHITECTURE §8.2, "URLs"). Path resolution is lexical; the file-system probe is only
/// called for paths inside the resource root, so a reference to <c>\\server\share</c> from a local document never causes
/// network access (no NTLM leak).
/// </summary>
internal sealed class ResourceUrlRewriter
{
    private const string VersionQuery = "?v=";
    private const string ReservedDomain = "mdreader.example";

    private readonly IFileSystemProbe _fileSystem;

    public ResourceUrlRewriter(IFileSystemProbe fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
    }

    /// img src / srcset candidate / source srcset candidate → URL to emit, or null to drop.
    public string? RewriteImageUrl(string rawUrl, RenderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var url = NormalizeUrl(rawUrl);
        if (url.Length == 0)
        {
            return null;
        }

        // data:image/* is displayed by <img> without script (even SVG); every other data: type is dropped.
        if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var reference = ReferenceParser.Parse(url);
        switch (reference.Kind)
        {
            case ReferenceKind.Https or ReferenceKind.Http when reference.AbsoluteUri is { } uri && IsReservedHost(uri):
                // The app's own virtual hosts. An author-written doc-host URL would otherwise skip the root and
                // forbidden-path checks (WebView2 served https://doc.mdreader.example/in.png%3Ahidden from the NTFS
                // stream <root>\in.png:hidden), so it is re-derived as the root-relative path it names; our own
                // rewritten URLs come back unchanged (second pass, idempotence). Any other reserved host is dropped,
                // and so is every doc-host URL when local resources are off (it names a local file).
                return context.AllowLocalResources && IsDocHost(uri) ? RewriteDocHostUrl(uri, context) : null;

            case ReferenceKind.Https:
                return url.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ? url : null;

            case ReferenceKind.Http:
                // The CSP only allows https images; Chromium would auto-upgrade anyway.
                return url.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ? "https" + url[4..] : null;

            case ReferenceKind.Relative:
            case ReferenceKind.RootRelative:
            case ReferenceKind.WindowsAbsolute:
            case ReferenceKind.File:
            case ReferenceKind.Unc:
                // Local resources off (web version, §15): dropped before any path resolution or probe call.
                return context.AllowLocalResources ? RewriteLocal(reference, context) : null;

            default:
                return null;
        }
    }

    /// a href → the raw value to keep, or null to drop the attribute (link text stays).
    /// Kept: fragment, relative, root-relative, UNC-looking, http(s):, mailto:, file:, and Windows absolute paths
    /// (<c>X:\</c>, <c>X:/</c>, Markdig's <c>X:%5C</c>); classification happens at click time (LinkClassifier).
    /// Everything else (javascript:, vbscript:, data:, unknown schemes, drive-relative) is dropped, and so are http(s)
    /// links to the app's own virtual hosts (LinkClassifier blocks them anyway; the app page itself is a navigation
    /// target the WebView allows).
    public static string? FilterHref(string rawHref)
    {
        var href = NormalizeUrl(rawHref);
        if (href.Length == 0 || !HasAllowedHrefScheme(href))
        {
            return null;
        }

        var reference = ReferenceParser.Parse(href);
        return reference.Kind switch
        {
            ReferenceKind.Http or ReferenceKind.Https when reference.AbsoluteUri is { } uri && IsReservedHost(uri) => null,
            ReferenceKind.Fragment or ReferenceKind.Relative or ReferenceKind.RootRelative or ReferenceKind.Unc
                or ReferenceKind.Http or ReferenceKind.Https or ReferenceKind.Mailto or ReferenceKind.File
                or ReferenceKind.WindowsAbsolute => href,
            _ => null,
        };
    }

    /// <summary>
    /// <c>mdreader.example</c> or any subdomain of it (the app's virtual hosts), checked on both the host and its IDNA
    /// (ASCII) form and ignoring a trailing dot. Same rule as LinkClassifier's.
    /// </summary>
    internal static bool IsReservedHost(Uri uri) => IsReservedHostName(uri.Host) || IsReservedHostName(uri.IdnHost);

    private static bool IsReservedHostName(string host)
    {
        var name = host.AsSpan().TrimEnd('.');
        return name.Equals(ReservedDomain, StringComparison.OrdinalIgnoreCase)
               || (name.EndsWith(ReservedDomain, StringComparison.OrdinalIgnoreCase) && name[^(ReservedDomain.Length + 1)] == '.');
    }

    private static bool IsDocHost(Uri uri) =>
        uri.Host.AsSpan().TrimEnd('.').Equals(ProtocolConstants.DocHost, StringComparison.OrdinalIgnoreCase)
        || uri.IdnHost.AsSpan().TrimEnd('.').Equals(ProtocolConstants.DocHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A doc-host URL names a path below the resource root: its (still percent-encoded) path is resolved like a
    /// root-relative reference, so IsWithin/IsForbiddenPath/probe apply and the canonical URL (+ <c>?v=</c>) is emitted.
    /// The author's query and fragment are dropped. "//x" paths classify as UNC and are dropped.
    /// </summary>
    private string? RewriteDocHostUrl(Uri uri, RenderContext context)
    {
        var path = ReferenceParser.Parse(uri.AbsolutePath);
        return path.Kind == ReferenceKind.RootRelative ? RewriteLocal(path, context) : null;
    }

    /// <summary>
    /// Applies the browser's URL pre-processing (WHATWG URL parser): strips leading/trailing C0 controls and spaces and
    /// removes every ASCII tab and newline. Classifying the string the browser will actually see defeats
    /// <c>jav&amp;#x09;ascript:</c>-style obfuscation.
    /// </summary>
    internal static string NormalizeUrl(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var span = raw.AsSpan();
        var start = 0;
        var end = span.Length;
        while (start < end && span[start] <= ' ')
        {
            start++;
        }

        while (end > start && span[end - 1] <= ' ')
        {
            end--;
        }

        span = span[start..end];
        if (span.IndexOfAny('\t', '\n', '\r') < 0)
        {
            return span.Length == raw.Length ? raw : span.ToString();
        }

        var buffer = new char[span.Length];
        var length = 0;
        foreach (var c in span)
        {
            if (c is not ('\t' or '\n' or '\r'))
            {
                buffer[length++] = c;
            }
        }

        return new string(buffer, 0, length);
    }

    /// <summary>
    /// Independent scheme check (defense in depth; does not rely on <see cref="ReferenceParser"/>). A WHATWG scheme is an
    /// ASCII letter followed by letters, digits, '+', '-' or '.', then ':'.
    /// </summary>
    private static bool HasAllowedHrefScheme(string href)
    {
        if (!char.IsAsciiLetter(href[0]))
        {
            return true;                                        // no scheme: fragment, relative, root-relative, UNC
        }

        var index = 1;
        while (index < href.Length && (char.IsAsciiLetterOrDigit(href[index]) || href[index] is '+' or '-' or '.'))
        {
            index++;
        }

        if (index >= href.Length || href[index] != ':')
        {
            return true;                                        // no scheme: relative path
        }

        var scheme = href.AsSpan(0, index);
        if (scheme.Length == 1)
        {
            // Drive letter: only absolute paths ("X:\", "X:/", and Markdig's percent-encoded "X:%5C"); "X:foo"
            // (drive-relative) is dropped.
            return ReferenceParser.Parse(href).Kind == ReferenceKind.WindowsAbsolute;
        }

        return scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("file", StringComparison.OrdinalIgnoreCase);
    }

    private string? RewriteLocal(ParsedReference reference, RenderContext context)
    {
        if (!context.AllowLocalResources)
        {
            return null;                                        // defense in depth: callers already checked
        }

        // Lexical only, and only through LocalPathResolver (it hides '~' from Path.GetFullPath's 8.3 expansion, which
        // would otherwise touch the network for UNC paths). No I/O happens before the IsWithin check below.
        var fullPath = LocalPathResolver.Resolve(reference, context.DocumentDirectory, context.ResourceRoot);
        if (fullPath is null
            || LocalPathResolver.IsForbiddenPath(fullPath)
            || !LocalPathResolver.IsWithin(fullPath, context.ResourceRoot))
        {
            return null;
        }

        var url = LocalPathResolver.ToDocHostUrl(fullPath, context.ResourceRoot);

        // The probe is called only for paths inside the resource root. The version query busts Blink's memory cache
        // after the file changed; unchanged images keep identical URLs. The author's query/fragment is dropped.
        return _fileSystem.GetLastWriteTimeUtc(fullPath) is { } lastWriteUtc
            ? url + VersionQuery + lastWriteUtc.Ticks.ToString("x", CultureInfo.InvariantCulture)
            : url;
    }
}
