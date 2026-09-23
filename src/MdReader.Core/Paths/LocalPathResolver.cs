using System.Text;
using MdReader.Core.Protocol;

namespace MdReader.Core.Paths;

/// <summary>
/// Resolves parsed references to local full paths and answers path questions. LEXICAL ONLY: never touches the file
/// system or the network (ARCHITECTURE §4.3, §8.2 "UNC / NTLM rule").
/// </summary>
/// <remarks>
/// The per-OS rules live in <see cref="PathPolicy"/>; this class is the resolution algorithm on top of them. Every
/// method takes an optional policy so both platforms can be exercised from one test run; omitting it uses
/// <see cref="PathPolicy.Current"/>, the policy for the running OS.
/// </remarks>
public static class LocalPathResolver
{
    /// <summary>
    /// Relative → documentDirectory; RootRelative → resourceRoot; WindowsAbsolute/File/Unc → as is. Then normalized
    /// the way the platform would (separators, <c>.</c>/<c>..</c> clamped at the volume root), without any I/O.
    /// Returns null for non-local kinds, for paths that are not fully qualified on this platform, and for forbidden
    /// paths (<see cref="IsForbiddenPath"/>).
    /// </summary>
    /// <remarks>A root-relative reference that climbs above the resource root ("/../x") is not clamped at the root;
    /// callers that need containment check <see cref="IsWithin"/>.</remarks>
    public static string? Resolve(ParsedReference reference, string documentDirectory, string resourceRoot,
                                  PathPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        policy ??= PathPolicy.Current;

        var path = reference.DecodedPath;
        if (path is null)
        {
            return null;
        }

        var candidate = reference.Kind switch
        {
            ReferenceKind.Relative => policy.Combine(documentDirectory, path),
            ReferenceKind.RootRelative => policy.Combine(resourceRoot, TrimLeadingSeparators(path, policy)),
            ReferenceKind.File => path,

            // A spelling this platform doesn't use names nothing here. Refusing it outright matters for Unc: on POSIX
            // the leading "//" of a protocol-relative "//host/x.png" would otherwise collapse to "/host/x.png".
            ReferenceKind.WindowsAbsolute => policy.SupportsDriveLetters ? path : null,
            ReferenceKind.Unc => policy.SupportsUncPaths ? path : null,
            _ => null,
        };

        if (candidate is null)
        {
            return null;
        }

        var fullPath = policy.NormalizeFullPath(candidate);
        return fullPath is null || policy.IsForbiddenPath(fullPath) ? null : fullPath;
    }

    /// <summary>
    /// True if <paramref name="fullPath"/> is <paramref name="rootFullPath"/> itself or lies below it. Separator-aware
    /// (<c>C:\foo</c> is not within <c>C:\fo</c>) and case-sensitive only where the platform is. Both paths are
    /// normalized lexically first, so <c>C:\root\..\x</c> is not within <c>C:\root</c>. False if either path is not a
    /// full path.
    /// </summary>
    public static bool IsWithin(string fullPath, string rootFullPath, PathPolicy? policy = null) =>
        (policy ?? PathPolicy.Current).IsWithin(fullPath, rootFullPath);

    /// <summary>True if reading the path could reach another machine: a UNC path on Windows, an automounted host
    /// under <c>/net</c> or <c>/Network</c> on macOS.</summary>
    public static bool IsNetworkPath(string fullPath, PathPolicy? policy = null) =>
        (policy ?? PathPolicy.Current).IsNetworkPath(fullPath);

    /// <summary>True if both paths are network paths that reach the same machine and share, compared strictly: equal
    /// except for ASCII letter case, so no Unicode case pair can name a different host. Different spellings of one
    /// server (IP, FQDN, trailing dot) count as different.</summary>
    public static bool IsSameNetworkRoot(string a, string b, PathPolicy? policy = null) =>
        (policy ?? PathPolicy.Current).IsSameNetworkRoot(a, b);

    /// <summary>
    /// True for paths MdReader must never open or map: on Windows the device namespaces (<c>\\?\</c>, <c>\\.\</c>,
    /// <c>\??\</c>), alternate data streams, reserved device names and characters that are invalid in a file name;
    /// on macOS resource forks and <c>/.vol</c>; on Linux <c>/dev</c>, <c>/proc</c> and <c>/sys</c>.
    /// Also true for null or empty input.
    /// </summary>
    public static bool IsForbiddenPath(string fullPath, PathPolicy? policy = null) =>
        (policy ?? PathPolicy.Current).IsForbiddenPath(fullPath);

    /// <summary>
    /// "https://doc.mdreader.example/" + each segment of the path relative to <paramref name="resourceRoot"/>,
    /// <see cref="Uri.EscapeDataString(string)"/>'d and joined with '/'. The root itself maps to the bare base URL.
    /// </summary>
    /// <exception cref="ArgumentException">A path is not a full path, or <paramref name="fullPath"/> is not within
    /// <paramref name="resourceRoot"/> (callers check <see cref="IsWithin"/> first).</exception>
    public static string ToDocHostUrl(string fullPath, string resourceRoot, PathPolicy? policy = null)
    {
        policy ??= PathPolicy.Current;
        var path = policy.NormalizeFullPath(fullPath)
                   ?? throw new ArgumentException("The path must be a full local path.", nameof(fullPath));
        var root = policy.NormalizeFullPath(resourceRoot)
                   ?? throw new ArgumentException("The resource root must be a full local path.", nameof(resourceRoot));
        if (!policy.IsWithinNormalized(path, root))
        {
            throw new ArgumentException("The path must be inside the resource root.", nameof(fullPath));
        }

        var relative = path.AsSpan(policy.TrimTrailingSeparators(root).Length);
        var builder = new StringBuilder(ProtocolConstants.DocBaseUrl, ProtocolConstants.DocBaseUrl.Length + (relative.Length * 2));
        var first = true;
        var start = 0;
        while (start <= relative.Length)
        {
            var end = start;
            while (end < relative.Length && !policy.IsDirectorySeparator(relative[end]))
            {
                end++;
            }

            var segment = relative[start..end];
            if (!segment.IsEmpty)
            {
                if (!first)
                {
                    builder.Append('/');
                }

                builder.Append(Uri.EscapeDataString(segment.ToString()));
                first = false;
            }

            start = end + 1;
        }

        return builder.ToString();
    }

    /// <summary>The canonical spelling of a fully qualified path, or null if it is not one. Purely lexical.</summary>
    internal static string? NormalizeFullPath(string? path, PathPolicy? policy = null) =>
        (policy ?? PathPolicy.Current).NormalizeFullPath(path);

    /// <summary>Equality of two paths after lexical normalization (trailing separators ignored).</summary>
    internal static bool PathEquals(string a, string b, PathPolicy? policy = null) =>
        (policy ?? PathPolicy.Current).PathEquals(a, b);

    private static string TrimLeadingSeparators(string path, PathPolicy policy)
    {
        var start = 0;
        while (start < path.Length && policy.IsDirectorySeparator(path[start]))
        {
            start++;
        }

        return start == 0 ? path : path[start..];
    }
}
