using System.Buffers;
using System.Text;
using MdReader.Core.Protocol;

namespace MdReader.Core.Paths;

/// <summary>
/// Resolves parsed references to local full paths and answers path questions. LEXICAL ONLY: never touches the file
/// system or the network (ARCHITECTURE §4.3, §8.2 "UNC / NTLM rule").
/// </summary>
/// <remarks>
/// <c>Path.GetFullPath</c> is NOT purely lexical: when the normalized path contains '~' it calls
/// <c>GetLongPathNameW</c> to expand 8.3 short names (verified: <c>C:\PROGRA~1\x</c> → <c>C:\Program Files\x</c>),
/// which for <c>\\server\share\a~1</c> would open an SMB connection and leak NTLM credentials. All normalization
/// here therefore goes through <see cref="NormalizeFullPath"/>, which hides '~' from that expansion.
/// </remarks>
public static class LocalPathResolver
{
    /// <summary>
    /// Stands in for '~' while calling <c>Path.GetFullPath</c>. '|' is invalid in Windows file names, so inputs that
    /// contain it are rejected, and it is an ordinary character to <c>GetFullPathNameW</c> (same trimming rules as '~').
    /// </summary>
    private const char ShortNamePlaceholder = '|';

    private static readonly SearchValues<char> InvalidPathChars = SearchValues.Create(
        "\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u0009\u000A\u000B\u000C\u000D\u000E\u000F" +
        "\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001A\u001B\u001C\u001D\u001E\u001F" +
        "<>\"|?*");

    // Win32 reserved device names (incl. COM0/LPT0 and the superscript-digit forms), matched on the part of a segment
    // before its first '.', with surrounding spaces ignored: "CON", "con.txt", "NUL .md", "COM¹.md" are all devices.
    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM\u00B9", "COM\u00B2", "COM\u00B3",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT\u00B9", "LPT\u00B2", "LPT\u00B3",
    };

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> ReservedDeviceNameLookup =
        ReservedDeviceNames.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>
    /// Relative → documentDirectory; RootRelative → resourceRoot; WindowsAbsolute/File/Unc → as is. Then normalized
    /// like <c>Path.GetFullPath</c> (separators, <c>.</c>/<c>..</c> clamped at the drive or share root, trailing
    /// dots/spaces), without any I/O. Returns null for non-local kinds, for paths that are not drive-absolute or
    /// <c>\\server\share</c>, and for forbidden paths (<see cref="IsForbiddenPath"/>).
    /// </summary>
    /// <remarks>A root-relative reference that climbs above the resource root ("/../x") is not clamped at the root;
    /// callers that need containment check <see cref="IsWithin"/>.</remarks>
    public static string? Resolve(ParsedReference reference, string documentDirectory, string resourceRoot)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var path = reference.DecodedPath;
        if (path is null)
        {
            return null;
        }

        var candidate = reference.Kind switch
        {
            ReferenceKind.Relative => Combine(documentDirectory, path),
            ReferenceKind.RootRelative => Combine(resourceRoot, path.TrimStart('\\', '/')),
            ReferenceKind.WindowsAbsolute or ReferenceKind.File or ReferenceKind.Unc => path,
            _ => null,
        };

        if (candidate is null)
        {
            return null;
        }

        var fullPath = NormalizeFullPath(candidate);
        return fullPath is null || IsForbiddenPath(fullPath) ? null : fullPath;
    }

    /// <summary>
    /// True if <paramref name="fullPath"/> is <paramref name="rootFullPath"/> itself or lies below it. Separator-aware
    /// (<c>C:\foo</c> is not within <c>C:\fo</c>), OrdinalIgnoreCase. Both paths are normalized lexically first, so
    /// <c>C:\root\..\x</c> is not within <c>C:\root</c>. False if either path is not a full local/UNC path.
    /// </summary>
    public static bool IsWithin(string fullPath, string rootFullPath)
    {
        var path = NormalizeFullPath(fullPath);
        var root = NormalizeFullPath(rootFullPath);
        return path is not null && root is not null && IsWithinNormalized(path, root);
    }

    /// <summary>True if the path starts with two separators ('\' or '/'): UNC, protocol-relative, or a device path.</summary>
    public static bool IsUncPath(string fullPath) =>
        fullPath is { Length: >= 2 } && IsSeparator(fullPath[0]) && IsSeparator(fullPath[1]);

    /// <summary>True if both paths are valid UNC paths with the same <c>\\server\share</c> prefix after lexical
    /// normalization. The prefix must be identical except for ASCII letter case (stricter than OrdinalIgnoreCase, so
    /// no Unicode case pair can name a different host). Different spellings of one server (IP, FQDN, trailing dot)
    /// count as different shares.</summary>
    public static bool IsSameUncShare(string a, string b)
    {
        var shareA = GetUncShare(NormalizeFullPath(a));
        var shareB = GetUncShare(NormalizeFullPath(b));
        return shareA is not null && shareB is not null && UncShareEquals(shareA, shareB);
    }

    /// <summary>
    /// True for paths MdReader must never open or map: device namespaces (<c>\\?\</c>, <c>\\.\</c>, <c>\??\</c>),
    /// alternate data streams (any ':' other than the drive designator), reserved device names in any segment
    /// (CON, PRN, AUX, NUL, CONIN$, CONOUT$, COM0–9, LPT0–9, with or without extension), and characters that are
    /// invalid in Windows file names (controls, <c>&lt; &gt; " | ? *</c>). Also true for null or empty input.
    /// </summary>
    public static bool IsForbiddenPath(string fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
        {
            return true;
        }

        var path = fullPath.AsSpan();

        // Device namespaces: \\?\ \\.\ (either separator), and the NT object namespace \??\.
        if (IsUncPath(fullPath))
        {
            var serverEnd = path[2..].IndexOfAny('\\', '/');
            var server = serverEnd < 0 ? path[2..] : path.Slice(2, serverEnd);
            if (server is "" or "." or ".." or "?")
            {
                return true;
            }
        }

        if (path.IndexOfAny(InvalidPathChars) >= 0)
        {
            return true;   // also covers \\?\ and \??\
        }

        // Alternate data streams: ':' is only allowed as the drive designator.
        var colonSearchStart = path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' ? 2 : 0;
        if (path[colonSearchStart..].Contains(':'))
        {
            return true;
        }

        foreach (var range in path.SplitAny('\\', '/'))
        {
            if (IsReservedDeviceName(path[range]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// "https://doc.mdreader.example/" + each segment of the path relative to <paramref name="resourceRoot"/>,
    /// <see cref="Uri.EscapeDataString(string)"/>'d and joined with '/'. The root itself maps to the bare base URL.
    /// </summary>
    /// <exception cref="ArgumentException">A path is not a full path, or <paramref name="fullPath"/> is not within
    /// <paramref name="resourceRoot"/> (callers check <see cref="IsWithin"/> first).</exception>
    public static string ToDocHostUrl(string fullPath, string resourceRoot)
    {
        var path = NormalizeFullPath(fullPath)
                   ?? throw new ArgumentException("The path must be a full local or UNC path.", nameof(fullPath));
        var root = NormalizeFullPath(resourceRoot)
                   ?? throw new ArgumentException("The resource root must be a full local or UNC path.", nameof(resourceRoot));
        if (!IsWithinNormalized(path, root))
        {
            throw new ArgumentException("The path must be inside the resource root.", nameof(fullPath));
        }

        var relative = path.AsSpan(TrimTrailingSeparators(root).Length);
        var builder = new StringBuilder(ProtocolConstants.DocBaseUrl, ProtocolConstants.DocBaseUrl.Length + (relative.Length * 2));
        var first = true;
        foreach (var range in relative.Split('\\'))
        {
            var segment = relative[range];
            if (segment.IsEmpty)
            {
                continue;
            }

            if (!first)
            {
                builder.Append('/');
            }

            builder.Append(Uri.EscapeDataString(segment.ToString()));
            first = false;
        }

        return builder.ToString();
    }

    /// <summary>
    /// Lexical equivalent of <c>Path.GetFullPath</c> for fully qualified paths (<c>X:\…</c> or <c>\\server\share…</c>):
    /// '/' → '\', runs of separators collapsed, <c>.</c>/<c>..</c> evaluated and clamped at the drive/share root,
    /// trailing dots and spaces trimmed as Win32 does. Never expands 8.3 short names, so no I/O happens.
    /// Returns null for null/empty, relative, drive-relative (<c>C:x</c>), device (<c>\\?\</c>, <c>\\.\</c>) paths,
    /// UNC paths without a valid server and share, and paths containing NUL or '|'.
    /// </summary>
    internal static string? NormalizeFullPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var candidate = path.Replace('/', '\\');
        if (candidate.AsSpan().IndexOfAny('\0', ShortNamePlaceholder) >= 0 || !HasValidFullPathRoot(candidate))
        {
            return null;
        }

        string normalized;
        try
        {
            normalized = Path.GetFullPath(candidate.Replace('~', ShortNamePlaceholder)).Replace(ShortNamePlaceholder, '~');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }

        return HasValidFullPathRoot(normalized) ? normalized : null;
    }

    /// <summary>OrdinalIgnoreCase equality of two paths after lexical normalization (trailing separators ignored).</summary>
    internal static bool PathEquals(string a, string b)
    {
        var left = NormalizeFullPath(a);
        var right = NormalizeFullPath(b);
        return left is not null && right is not null
               && TrimTrailingSeparators(left).Equals(TrimTrailingSeparators(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWithinNormalized(string path, string root)
    {
        var trimmedRoot = TrimTrailingSeparators(root);
        var trimmedPath = TrimTrailingSeparators(path);
        if (!trimmedPath.StartsWith(trimmedRoot, StringComparison.OrdinalIgnoreCase)
            || (trimmedPath.Length != trimmedRoot.Length && trimmedPath[trimmedRoot.Length] != '\\'))
        {
            return false;
        }

        // For a UNC root the \\server\share part decides which host is contacted: require the strict comparison.
        var rootShare = GetUncShare(trimmedRoot);
        return rootShare is null || UncShareEquals(trimmedPath.AsSpan(0, rootShare.Length), rootShare);
    }

    /// <summary>Equal except for ASCII letter case; any non-ASCII character must match exactly.</summary>
    private static bool UncShareEquals(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (var i = 0; i < a.Length; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x != y && !(char.IsAsciiLetter(x) && char.IsAsciiLetter(y) && (x | 0x20) == (y | 0x20)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>"\\server\share" of a normalized UNC path, or null.</summary>
    private static string? GetUncShare(string? normalizedPath)
    {
        if (normalizedPath is null || !IsUncPath(normalizedPath))
        {
            return null;
        }

        var serverEnd = normalizedPath.IndexOf('\\', 2);
        if (serverEnd < 0)
        {
            return null;
        }

        var shareEnd = normalizedPath.IndexOf('\\', serverEnd + 1);
        return shareEnd < 0 ? normalizedPath : normalizedPath[..shareEnd];
    }

    /// <summary>"X:\" or "\\server\share" with a server and share that are not empty, ".", ".." or "?".</summary>
    private static bool HasValidFullPathRoot(string path)
    {
        if (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')
        {
            return true;
        }

        if (path.Length < 2 || path[0] != '\\' || path[1] != '\\')
        {
            return false;
        }

        var serverEnd = path.IndexOf('\\', 2);
        if (serverEnd < 0 || !IsValidUncComponent(path.AsSpan(2, serverEnd - 2)))
        {
            return false;
        }

        var shareEnd = path.IndexOf('\\', serverEnd + 1);
        var share = shareEnd < 0 ? path.AsSpan(serverEnd + 1) : path.AsSpan(serverEnd + 1, shareEnd - serverEnd - 1);
        return IsValidUncComponent(share);
    }

    private static bool IsValidUncComponent(ReadOnlySpan<char> component) =>
        component is not ("" or "." or ".." or "?");

    private static bool IsReservedDeviceName(ReadOnlySpan<char> segment)
    {
        var dot = segment.IndexOf('.');
        var baseName = (dot < 0 ? segment : segment[..dot]).Trim(' ');
        return baseName.Length is >= 3 and <= 7 && ReservedDeviceNameLookup.Contains(baseName);
    }

    private static string? Combine(string? baseDirectory, string relativePath)
    {
        if (string.IsNullOrEmpty(baseDirectory))
        {
            return null;
        }

        return TrimTrailingSeparators(baseDirectory) + "\\" + relativePath;
    }

    private static string TrimTrailingSeparators(string path) => path.TrimEnd('\\', '/');

    private static bool IsSeparator(char c) => c is '\\' or '/';
}
