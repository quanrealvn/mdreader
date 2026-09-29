using System.Buffers;
using System.Text;

namespace MdReader.Core.Paths;

/// <summary>
/// Windows path rules: drive letters and UNC shares, '\' and '/' both separators, case-insensitive comparison,
/// reserved device names, and the device namespaces MdReader must never open.
/// </summary>
/// <remarks>
/// <para><b>Why normalization is hand-written.</b> <c>Path.GetFullPath</c> is not purely lexical: when the path
/// contains '~' it calls <c>GetLongPathNameW</c> to expand 8.3 short names, and for <c>\\server\share\a~1</c> that
/// opens an SMB connection and leaks NTLM credentials (ARCHITECTURE §8.2). It also answers for whatever OS the process
/// runs on, so it can't decide what a Windows path means while the suite runs on a Mac.
/// <see cref="NormalizeFullPath"/> therefore reproduces <c>GetFullPathNameW</c>'s canonicalization itself;
/// <c>WindowsPathPolicyEquivalenceTests</c> asserts the two agree character for character over a large corpus.</para>
/// <para><b>The rules it reproduces</b>, in the order they apply, for the part of the path below the root:
/// runs of separators collapse; a <c>.</c> component is dropped and a <c>..</c> component pops, clamped at the drive or
/// share root; a component that is followed by a separator and ends in exactly one '.' loses that dot
/// (<c>b.\c</c> → <c>b\c</c>, while <c>b..\c</c> keeps both); and the component at the very end of the path loses all
/// its trailing dots and spaces, which can leave it empty and the path ending in a separator
/// (<c>C:\a\b\...</c> → <c>C:\a\b\</c>).</para>
/// </remarks>
internal sealed class WindowsPathPolicy : PathPolicy
{
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

    public override string Name => "Windows";

    public override char DirectorySeparator => '\\';

    public override bool IsCaseSensitive => false;

    public override int MaxMappableRootLength => ResourceRootResolver.MaxMappablePathLength;

    public override bool SupportsDriveLetters => true;

    public override bool SupportsUncPaths => true;

    public override bool IsDirectorySeparator(char value) => value is '\\' or '/';

    public override bool IsFullyQualified(string? path) =>
        path is not null
        && ((path.Length >= 2 && IsDirectorySeparator(path[0]) && IsDirectorySeparator(path[1]))
            || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && IsDirectorySeparator(path[2])));

    public override string? NormalizeFullPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var candidate = path.Replace('/', '\\');

        // '\0' can never name a file. '|' is rejected here as well as in IsForbiddenPath so that IsWithin and
        // PathEquals never accept a path the resolver would refuse.
        if (candidate.AsSpan().IndexOfAny('\0', '|') >= 0 || !HasValidFullPathRoot(candidate))
        {
            return null;
        }

        var normalized = Canonicalize(candidate);
        return normalized is not null && HasValidFullPathRoot(normalized) ? normalized : null;
    }

    /// <summary>
    /// True for device namespaces (<c>\\?\</c>, <c>\\.\</c>, <c>\??\</c>), alternate data streams (any ':' other than
    /// the drive designator), reserved device names in any segment, and characters that are invalid in Windows file
    /// names (controls, <c>&lt; &gt; " | ? *</c>). Also true for null or empty input.
    /// </summary>
    public override bool IsForbiddenPath(string? fullPath)
    {
        if (string.IsNullOrEmpty(fullPath))
        {
            return true;
        }

        var path = fullPath.AsSpan();

        // Device namespaces: \\?\ \\.\ (either separator), and the NT object namespace \??\.
        if (IsNetworkPath(fullPath))
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
            if (IsReservedName(path[range]))
            {
                return true;
            }
        }

        return false;
    }

    public override bool IsReservedName(ReadOnlySpan<char> segment)
    {
        var dot = segment.IndexOf('.');
        var baseName = (dot < 0 ? segment : segment[..dot]).Trim(' ');
        return baseName.Length is >= 3 and <= 7 && ReservedDeviceNameLookup.Contains(baseName);
    }

    /// <summary>Always false: on Windows "hidden" is a file attribute, not something a name can express.</summary>
    public override bool IsHiddenName(ReadOnlySpan<char> name) => false;

    /// <summary>True if the path starts with two separators: UNC, protocol-relative, or a device path.</summary>
    public override bool IsNetworkPath(string? path) =>
        path is { Length: >= 2 } && IsDirectorySeparator(path[0]) && IsDirectorySeparator(path[1]);

    public override string? GetNetworkRoot(string? normalizedFullPath)
    {
        if (normalizedFullPath is null || !IsNetworkPath(normalizedFullPath))
        {
            return null;
        }

        var serverEnd = normalizedFullPath.IndexOf('\\', 2);
        if (serverEnd < 0)
        {
            return null;
        }

        var shareEnd = normalizedFullPath.IndexOf('\\', serverEnd + 1);
        return shareEnd < 0 ? normalizedFullPath : normalizedFullPath[..shareEnd];
    }

    public override string? GetVolumeRoot(string? normalizedFullPath)
    {
        if (string.IsNullOrEmpty(normalizedFullPath))
        {
            return null;
        }

        var rootLength = GetRootLength(normalizedFullPath);
        return rootLength <= 0 ? null : normalizedFullPath[..rootLength];
    }

    public override string ToLocalSeparators(string path) => path.Replace('/', '\\');

    public override string GetFileUriPath(Uri uri)
    {
        var path = ToLocalSeparators(Uri.UnescapeDataString(uri.AbsolutePath));
        if (uri.Host.Length > 0)
        {
            return @"\\" + uri.Host + path;
        }

        // "file:///C:/x" → "\C:\x": drop the leading separator so the drive designator starts the path.
        return path.Length >= 3 && char.IsAsciiLetter(path[1]) && path[2] == ':' ? path[1..] : path;
    }

    protected override int GetRootLength(string normalizedFullPath)
    {
        if (normalizedFullPath.Length >= 3 && char.IsAsciiLetter(normalizedFullPath[0])
            && normalizedFullPath[1] == ':' && IsDirectorySeparator(normalizedFullPath[2]))
        {
            return 3;
        }

        return GetNetworkRoot(normalizedFullPath)?.Length ?? 0;
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

    /// <summary>
    /// The lexical half of <c>GetFullPathNameW</c> for a path that already passed <see cref="HasValidFullPathRoot"/>
    /// and has only '\' separators. Null if the result can no longer be a full path.
    /// </summary>
    private static string? Canonicalize(string path)
    {
        string root;
        string rest;
        var shareStart = -1;
        if (path[0] == '\\')
        {
            var serverEnd = path.IndexOf('\\', 2);
            shareStart = serverEnd + 1;
            var shareEnd = path.IndexOf('\\', shareStart);
            root = shareEnd < 0 ? path : path[..shareEnd];
            rest = shareEnd < 0 ? string.Empty : path[(shareEnd + 1)..];
        }
        else
        {
            root = path[..3];
            rest = path[3..];
        }

        var segments = new List<string>();
        var start = 0;
        while (start <= rest.Length)
        {
            var separator = rest.IndexOf('\\', start);
            var end = separator < 0 ? rest.Length : separator;
            var segment = rest.AsSpan(start, end - start);
            if (!segment.IsEmpty)
            {
                ApplySegment(segments, segment);
            }

            if (separator < 0)
            {
                break;
            }

            start = separator + 1;
        }

        // Nothing follows the last component, so it loses all its trailing dots and spaces. Note that this runs on
        // what the components above left behind: "C:\a. \b\.." drops "b", and "a. " is then what the path ends with,
        // so the result is "C:\a".
        var endsWithSeparator = path.Length > root.Length && path[^1] == '\\';
        if (!endsWithSeparator)
        {
            if (segments.Count > 0)
            {
                var last = segments[^1].AsSpan().TrimEnd(" .");
                segments.RemoveAt(segments.Count - 1);
                if (last.IsEmpty)
                {
                    endsWithSeparator = true;   // "C:\a\b\..." and "C:\a\b\ " both end at the folder
                }
                else
                {
                    segments.Add(last.ToString());
                }
            }
            else if (shareStart >= 0)
            {
                // Everything below the share was consumed, so the share itself is what the path ends with.
                var share = root.AsSpan(shareStart).TrimEnd(" .");
                if (share.IsEmpty)
                {
                    return null;
                }

                root = string.Concat(root.AsSpan(0, shareStart), share);
            }
        }

        var builder = new StringBuilder(path.Length);
        builder.Append(root);
        foreach (var segment in segments)
        {
            if (builder[^1] != '\\')
            {
                builder.Append('\\');
            }

            builder.Append(segment);
        }

        if (endsWithSeparator && builder[^1] != '\\')
        {
            builder.Append('\\');
        }

        return builder.ToString();
    }

    private static void ApplySegment(List<string> segments, ReadOnlySpan<char> segment)
    {
        if (segment is ".")
        {
            return;
        }

        if (segment is "..")
        {
            if (segments.Count > 0)
            {
                segments.RemoveAt(segments.Count - 1);   // clamped at the drive or share root
            }

            return;
        }

        // A component loses a single trailing dot ("b.\c" → "b\c"), but keeps two or more ("b..\c" is literal).
        var trimmed = segment.Length >= 2 && segment[^1] == '.' && segment[^2] != '.' ? segment[..^1] : segment;
        segments.Add(trimmed.ToString());
    }
}
