using MdReader.Core.Paths;
using MdReader.Core.Protocol;

namespace MdReader.Mac;

/// <summary>Why a request to one of the two virtual hosts was refused.</summary>
public enum ResourceRefusal
{
    /// The request resolved to a file that may be served.
    None,

    /// Not one of the two hosts, not our scheme, or a URL with a port or credentials in it.
    UnknownHost,

    /// The document host, before its folder has been mapped (or after mapping failed).
    NotMapped,

    /// A path the URL can't legally name: a bare directory, an empty or dot segment, or a separator smuggled in
    /// percent-encoded.
    BadPath,

    /// The path climbs out of the host's root.
    OutsideRoot,

    /// A path the platform's rules forbid (a macOS resource fork, <c>/.vol</c>, a Windows device name).
    Forbidden,

    /// Inside the root and allowed, but there is no such file.
    NotFound,
}

/// <summary>A file the scheme handler may serve.</summary>
public readonly record struct HostedResource(string FullPath, string MediaType);

/// <summary>
/// The URL-to-file rules of the two virtual hosts, and the whole of their confinement. This is the macOS counterpart
/// of <c>CoreWebView2.SetVirtualHostNameToFolderMapping</c>: WebView2 does the mapping, the traversal check and the
/// forbidden-name check inside the browser, and on macOS they are ours to get right, so they live here as pure
/// string and path work with no WebKit type in sight.
/// </summary>
/// <remarks>
/// <para>The rules, in order: our scheme, a known host, a root that is mapped, a path whose every segment survives
/// percent-decoding without becoming a separator or a dot segment, a result inside the root, a result the path policy
/// doesn't forbid, and a file that exists. Anything else is a refusal, and a refusal is served as a failure, never as
/// a redirect or a directory listing.</para>
/// <para>The <c>..</c> check is deliberately not "normalize and compare": a decoded segment that <i>is</i> a dot
/// segment is rejected outright, so no traversal is ever resolved, and the <see cref="PathPolicy.IsWithin"/> check
/// afterwards is a second, independent line.</para>
/// </remarks>
public sealed class HostedResourceResolver
{
    private readonly PathPolicy _policy;
    private readonly IFileSystemProbe _fileSystem;

    public HostedResourceResolver(IFileSystemProbe fileSystem, PathPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
        _policy = policy ?? PathPolicy.Current;
    }

    /// <summary>The app's own folder (<c>AppPaths.WebRoot</c>), served to the app host. Never null in practice.</summary>
    public string? AppRoot { get; set; }

    /// <summary>The document's resource root, served to the document host; null until it has been mapped.</summary>
    public string? DocumentRoot { get; set; }

    /// <summary>Resolves one request. <paramref name="resource"/> is meaningful only when the result is
    /// <see cref="ResourceRefusal.None"/>.</summary>
    public ResourceRefusal Resolve(string? url, out HostedResource resource)
    {
        resource = default;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return ResourceRefusal.UnknownHost;
        }

        if (!string.Equals(uri.Scheme, ProtocolConstants.WebScheme, StringComparison.OrdinalIgnoreCase)
            || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return ResourceRefusal.UnknownHost;
        }

        string? root;
        if (string.Equals(uri.Host, ProtocolConstants.AppHost, StringComparison.OrdinalIgnoreCase))
        {
            root = AppRoot;
        }
        else if (string.Equals(uri.Host, ProtocolConstants.DocHost, StringComparison.OrdinalIgnoreCase))
        {
            root = DocumentRoot;
            if (root is null)
            {
                return ResourceRefusal.NotMapped;
            }
        }
        else
        {
            return ResourceRefusal.UnknownHost;
        }

        if (string.IsNullOrEmpty(root))
        {
            return ResourceRefusal.NotMapped;
        }

        // AbsolutePath keeps the percent-encoding, which is what we want: every segment is decoded on its own, so
        // "%2F" can never turn into a separator and "%2E%2E" can never turn into a climb.
        if (!TryBuildPath(uri.AbsolutePath, root, out string? fullPath))
        {
            return ResourceRefusal.BadPath;
        }

        if (_policy.IsForbiddenPath(fullPath))
        {
            return ResourceRefusal.Forbidden;
        }

        if (!_policy.IsWithin(fullPath, root))
        {
            return ResourceRefusal.OutsideRoot;
        }

        if (!_fileSystem.FileExists(fullPath))
        {
            return ResourceRefusal.NotFound;
        }

        resource = new HostedResource(fullPath, MediaTypes.ForPath(fullPath));
        return ResourceRefusal.None;
    }

    private bool TryBuildPath(string absolutePath, string root, out string fullPath)
    {
        fullPath = string.Empty;
        if (absolutePath.EndsWith('/'))
        {
            return false;   // names a directory, and there are no directory listings here
        }

        string current = root;
        var any = false;
        foreach (Range range in absolutePath.AsSpan().Split('/'))
        {
            ReadOnlySpan<char> encoded = absolutePath.AsSpan()[range];
            if (encoded.IsEmpty)
            {
                continue;   // leading '/', and a trailing one that would name a directory (rejected below)
            }

            string segment;
            try
            {
                segment = Uri.UnescapeDataString(encoded.ToString());
            }
            catch (UriFormatException)
            {
                return false;
            }

            if (segment.Length == 0 || segment is "." or ".." || segment.AsSpan().IndexOfAny('/', '\\', '\0') >= 0)
            {
                return false;
            }

            current = _policy.Join(current, segment);
            any = true;
        }

        if (!any)
        {
            return false;   // the root itself: a directory, never a resource
        }

        fullPath = current;
        return true;
    }
}

/// <summary>
/// Media types for the files the two hosts serve. The list is an allow-list rather than a lookup in the system's
/// database on purpose: the page's CSP distinguishes scripts from styles from images, so a wrong or missing type is a
/// blank page, and a document folder must never be able to introduce a type by adding a file.
/// </summary>
public static class MediaTypes
{
    /// <summary>What an unknown extension is served as: never executable, never a stylesheet.</summary>
    public const string Default = "application/octet-stream";

    public static string ForPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        int dot = path.LastIndexOf('.');
        if (dot < 0)
        {
            return Default;
        }

        return path.AsSpan(dot + 1).ToString().ToLowerInvariant() switch
        {
            "html" or "htm" => "text/html; charset=utf-8",
            "css" => "text/css; charset=utf-8",
            "js" or "mjs" => "text/javascript; charset=utf-8",
            "json" or "map" => "application/json; charset=utf-8",
            "svg" => "image/svg+xml",
            "png" => "image/png",
            "jpg" or "jpeg" => "image/jpeg",
            "gif" => "image/gif",
            "webp" => "image/webp",
            "avif" => "image/avif",
            "bmp" => "image/bmp",
            "ico" => "image/x-icon",
            "woff" => "font/woff",
            "woff2" => "font/woff2",
            "ttf" => "font/ttf",
            "otf" => "font/otf",
            "txt" or "md" or "markdown" => "text/plain; charset=utf-8",
            _ => Default,
        };
    }
}
