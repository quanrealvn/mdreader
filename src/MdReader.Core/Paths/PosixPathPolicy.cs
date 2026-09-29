using System.Text;

namespace MdReader.Core.Paths;

/// <summary>
/// Generic POSIX path rules (Linux): one root, '/' the only separator, case-sensitive names, and no name the kernel
/// reserves. Only NUL and '/' are illegal in a file name, so ':', '*', '?', '&lt;', '|' and trailing dots and spaces
/// are all ordinary characters here and must survive normalization unchanged.
/// </summary>
/// <remarks>
/// Normalization is hand-written for the same reason as on Windows (see <see cref="WindowsPathPolicy"/>): it has to be
/// purely lexical, and it has to give the same answer whatever OS the process runs on. <c>Path.GetFullPath("/a/b")</c>
/// on a Windows host would prepend the current drive.
/// </remarks>
internal class PosixPathPolicy : PathPolicy
{
    /// <summary>PATH_MAX. There is no MAX_PATH-style limit on mapping a folder into a custom URL scheme.</summary>
    private const int PathMax = 1024;

    public override string Name => "POSIX";

    public override char DirectorySeparator => '/';

    public override bool IsCaseSensitive => true;

    public override int MaxMappableRootLength => PathMax;

    public override bool SupportsDriveLetters => false;

    public override bool SupportsUncPaths => false;

    public override bool IsDirectorySeparator(char value) => value == '/';

    public override bool IsFullyQualified(string? path) => path is { Length: > 0 } && path[0] == '/';

    public override string? NormalizeFullPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/' || path.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        var endsWithSeparator = path.Length > 1 && path[^1] == '/';
        var segments = new List<string>();
        var start = 1;
        while (start <= path.Length)
        {
            var separator = path.IndexOf('/', start);
            var end = separator < 0 ? path.Length : separator;
            var segment = path.AsSpan(start, end - start);
            if (segment is "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);   // clamped at "/"
                }
            }
            else if (!segment.IsEmpty && segment is not ".")
            {
                segments.Add(segment.ToString());
            }

            if (separator < 0)
            {
                break;
            }

            start = separator + 1;
        }

        if (segments.Count == 0)
        {
            return "/";
        }

        var builder = new StringBuilder(path.Length);
        foreach (var segment in segments)
        {
            builder.Append('/').Append(segment);
        }

        if (endsWithSeparator)
        {
            builder.Append('/');
        }

        return builder.ToString();
    }

    /// <summary>
    /// True for null or empty input, for paths containing NUL, and for the kernel's own namespaces
    /// (<see cref="ForbiddenRoots"/>), which are not files a Markdown document may point at.
    /// </summary>
    public override bool IsForbiddenPath(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath) || fullPath.Contains('\0', StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var root in ForbiddenRoots)
        {
            if (fullPath.StartsWith(root, Comparison)
                && (fullPath.Length == root.Length || fullPath[root.Length] == '/'))
            {
                return true;
            }
        }

        foreach (var range in fullPath.AsSpan().Split('/'))
        {
            if (IsReservedName(fullPath.AsSpan()[range]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Nothing is reserved on plain POSIX: "CON", "NUL" and "COM1" are ordinary file names.</summary>
    public override bool IsReservedName(ReadOnlySpan<char> segment) => false;

    public override bool IsHiddenName(ReadOnlySpan<char> name) => name.Length > 0 && name[0] == '.';

    public override bool IsNetworkPath(string? path) => false;

    public override string? GetNetworkRoot(string? normalizedFullPath) => null;

    public override string? GetVolumeRoot(string? normalizedFullPath) =>
        IsFullyQualified(normalizedFullPath) ? "/" : null;

    /// <summary>Unchanged: '\' is a perfectly ordinary character in a POSIX file name, not a separator.</summary>
    public override string ToLocalSeparators(string path) => path;

    public override string GetFileUriPath(Uri uri)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath);

        // "file://host/share/x": there is no UNC here, so keep the two leading slashes. ReferenceParser classifies
        // that as Unc and the resolver refuses it, which is what we want for a host we would have to reach.
        return uri.Host.Length > 0 ? "//" + uri.Host + path : path;
    }

    /// <summary>Path prefixes that are the kernel's, not the file system's.</summary>
    protected virtual string[] ForbiddenRoots { get; } = ["/dev", "/proc", "/sys"];

    protected override int GetRootLength(string normalizedFullPath) => normalizedFullPath[0] == '/' ? 1 : 0;

    /// <summary>The component of <paramref name="path"/> that follows <paramref name="prefix"/>, or an empty span.</summary>
    private protected ReadOnlySpan<char> FirstComponentUnder(string path, string prefix)
    {
        if (!path.StartsWith(prefix, Comparison) || path.Length <= prefix.Length || path[prefix.Length] != '/')
        {
            return default;
        }

        var rest = path.AsSpan(prefix.Length + 1);
        var separator = rest.IndexOf('/');
        return separator < 0 ? rest : rest[..separator];
    }

    /// <summary>True if <paramref name="path"/> is <paramref name="prefix"/> itself or lies below it.</summary>
    private protected bool IsUnder(string? path, string prefix) =>
        path is not null && path.StartsWith(prefix, Comparison)
        && (path.Length == prefix.Length || path[prefix.Length] == '/');
}
