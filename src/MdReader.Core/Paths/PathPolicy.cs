namespace MdReader.Core.Paths;

/// <summary>
/// Everything about a file path that the operating system decides: what counts as a separator, what a fully qualified
/// path looks like, whether two spellings name the same file, which names the kernel reserves, and which prefixes can
/// reach the network. <see cref="LocalPathResolver"/> and the rest of <c>MdReader.Core.Paths</c> ask a policy rather
/// than assuming Windows (ARCHITECTURE §4.3, §8.2).
/// </summary>
/// <remarks>
/// <para><b>Lexical only.</b> Every member here is a pure function of its arguments. Nothing touches the file system or
/// the network, because the strings come from untrusted Markdown: on Windows <c>Path.GetFullPath</c> expands 8.3 short
/// names through <c>GetLongPathNameW</c> for any path containing '~', which opens an SMB connection for
/// <c>\\server\share\a~1</c> and leaks NTLM credentials. The Windows policy therefore normalizes paths itself instead of
/// calling <c>Path.GetFullPath</c>, and the POSIX policies do the same so the answer never depends on the host OS.</para>
/// <para>The policies are stateless singletons. <see cref="Current"/> is the one that matches the running OS; tests
/// exercise <see cref="Windows"/>, <see cref="MacOs"/> and <see cref="Posix"/> directly, on any host.</para>
/// </remarks>
public abstract class PathPolicy
{
    /// <summary>Windows: drive letters, UNC shares, backslashes, reserved device names, case-insensitive.</summary>
    public static PathPolicy Windows { get; } = new WindowsPathPolicy();

    /// <summary>macOS: POSIX plus <c>/Volumes</c>, resource forks and case-insensitive-but-case-preserving volumes.</summary>
    public static PathPolicy MacOs { get; } = new MacOsPathPolicy();

    /// <summary>Generic POSIX (Linux): one root, '/' only, case-sensitive, no reserved names.</summary>
    public static PathPolicy Posix { get; } = new PosixPathPolicy();

    /// <summary>The policy for the running operating system.</summary>
    public static PathPolicy Current { get; } = Detect();

    /// <summary>Short name used in logs and test failure messages.</summary>
    public abstract string Name { get; }

    /// <summary>The separator this OS writes when it builds a path.</summary>
    public abstract char DirectorySeparator { get; }

    /// <summary>True if the file system distinguishes "README.md" from "readme.md".</summary>
    /// <remarks>macOS says false: APFS and HFS+ are case-insensitive but case-preserving by default. A volume formatted
    /// case-sensitive would make containment checks slightly generous (two differently-cased sibling folders compare
    /// equal), which can only ever let a document reach another file under its own resource root.</remarks>
    public abstract bool IsCaseSensitive { get; }

    /// <summary>Longest resource root the platform's web view can map (ARCHITECTURE §4.3).</summary>
    public abstract int MaxMappableRootLength { get; }

    /// <summary>True where <c>C:\dir</c> names a location. False elsewhere, so a document written on Windows resolves
    /// to nothing rather than to a folder that happens to be called "C:".</summary>
    public abstract bool SupportsDriveLetters { get; }

    /// <summary>True where <c>\\server\share</c> names a location. False elsewhere, so a protocol-relative
    /// <c>//host/x.png</c> never collapses into an ordinary absolute path.</summary>
    public abstract bool SupportsUncPaths { get; }

    /// <summary>Comparison for whole paths and path segments.</summary>
    public StringComparison Comparison => IsCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary><see cref="Comparison"/> as a comparer, for dictionaries and sets keyed by path.</summary>
    public StringComparer Comparer => IsCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public abstract bool IsDirectorySeparator(char value);

    /// <summary>True if the path names a location without needing a current directory (<c>C:\x</c>, <c>\\srv\share</c>, <c>/x</c>).</summary>
    public abstract bool IsFullyQualified(string? path);

    /// <summary>
    /// The canonical spelling of a fully qualified path: separators unified and collapsed, <c>.</c>/<c>..</c> resolved
    /// and clamped at the volume root, plus whatever else the platform strips. Null if the path is not fully qualified
    /// or can never name a file. Purely lexical, so symlinks and short names are left alone.
    /// </summary>
    public abstract string? NormalizeFullPath(string? path);

    /// <summary>True for paths MdReader must never open or map, and for null/empty input.</summary>
    public abstract bool IsForbiddenPath(string? fullPath);

    /// <summary>True if the segment is a name the kernel reserves (Windows device names; none on POSIX).</summary>
    public abstract bool IsReservedName(ReadOnlySpan<char> segment);

    /// <summary>True if the platform's naming convention marks the file hidden (a leading '.' on POSIX).</summary>
    /// <remarks>Windows always says false: there hidden is a file attribute, not something the name can express.</remarks>
    public abstract bool IsHiddenName(ReadOnlySpan<char> name);

    /// <summary>
    /// True if reading the path could reach another machine (a UNC path on Windows, an automounted host on macOS).
    /// Checked lexically before any probe so a hostile document can't make us open a connection.
    /// </summary>
    public abstract bool IsNetworkPath(string? path);

    /// <summary>The prefix that decides which machine a network path reaches (<c>\\server\share</c>), or null.</summary>
    public abstract string? GetNetworkRoot(string? normalizedFullPath);

    /// <summary>The volume the path lives on (<c>C:\</c>, <c>\\srv\share</c>, <c>/</c>, <c>/Volumes/Backup</c>), or null.</summary>
    public abstract string? GetVolumeRoot(string? normalizedFullPath);

    /// <summary>'/' → the platform separator, for local kinds of <see cref="ParsedReference"/>.</summary>
    public abstract string ToLocalSeparators(string path);

    /// <summary>The local path a <c>file:</c> URI names on this platform, without asking the host OS.</summary>
    /// <remarks><c>Uri.LocalPath</c> answers for whatever OS the process runs on, so it can't be used to decide what a
    /// Windows document means while the tests run on a Mac.</remarks>
    public abstract string GetFileUriPath(Uri uri);

    /// <summary>True if both paths are network paths that reach the same machine and share.</summary>
    public bool IsSameNetworkRoot(string? a, string? b)
    {
        var rootA = GetNetworkRoot(NormalizeFullPath(a));
        var rootB = GetNetworkRoot(NormalizeFullPath(b));
        return rootA is not null && rootB is not null && NetworkRootEquals(rootA, rootB);
    }

    /// <summary>
    /// True if <paramref name="fullPath"/> is <paramref name="rootFullPath"/> itself or lies below it. Separator-aware
    /// (<c>C:\foo</c> is not within <c>C:\fo</c>). Both paths are normalized first, so <c>C:\root\..\x</c> is not within
    /// <c>C:\root</c>. False if either is not a fully qualified path.
    /// </summary>
    public bool IsWithin(string? fullPath, string? rootFullPath)
    {
        var path = NormalizeFullPath(fullPath);
        var root = NormalizeFullPath(rootFullPath);
        return path is not null && root is not null && IsWithinNormalized(path, root);
    }

    /// <summary>Equality of two paths after normalization, ignoring trailing separators.</summary>
    public bool PathEquals(string? a, string? b)
    {
        var left = NormalizeFullPath(a);
        var right = NormalizeFullPath(b);
        return left is not null && right is not null
               && TrimTrailingSeparators(left).Equals(TrimTrailingSeparators(right), Comparison);
    }

    /// <summary>Appends a relative path to a directory without normalizing either; null for an empty directory.</summary>
    public string? Combine(string? baseDirectory, string relativePath) =>
        string.IsNullOrEmpty(baseDirectory) ? null : TrimTrailingSeparators(baseDirectory) + DirectorySeparator + relativePath;

    /// <summary>A single child of a directory, the way <c>Path.Join</c> would build it on that platform.</summary>
    public string Join(string directory, string name) => TrimTrailingSeparators(directory) + DirectorySeparator + name;

    /// <summary>The directory containing <paramref name="normalizedFullPath"/>, or null at the file system root.</summary>
    public string? GetDirectoryName(string? normalizedFullPath)
    {
        if (string.IsNullOrEmpty(normalizedFullPath))
        {
            return null;
        }

        var rootLength = GetRootLength(normalizedFullPath);
        if (rootLength <= 0)
        {
            return null;
        }

        var end = normalizedFullPath.Length;
        while (end > rootLength && IsDirectorySeparator(normalizedFullPath[end - 1]))
        {
            end--;
        }

        while (end > rootLength && !IsDirectorySeparator(normalizedFullPath[end - 1]))
        {
            end--;
        }

        if (end <= rootLength)
        {
            // The path is a direct child of the root ("C:\x", "/x"), or the root itself.
            return normalizedFullPath.Length > rootLength ? normalizedFullPath[..rootLength] : null;
        }

        return normalizedFullPath[..(end - 1)];
    }

    /// <summary>The last segment of the path, ignoring trailing separators.</summary>
    public string GetFileName(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var end = path.Length;
        while (end > 0 && IsDirectorySeparator(path[end - 1]))
        {
            end--;
        }

        var start = end;
        while (start > 0 && !IsDirectorySeparator(path[start - 1]))
        {
            start--;
        }

        return path[start..end];
    }

    public string TrimTrailingSeparators(string path)
    {
        var end = path.Length;
        while (end > 0 && IsDirectorySeparator(path[end - 1]))
        {
            end--;
        }

        return end == path.Length ? path : path[..end];
    }

    /// <summary>Length of the part of a normalized path that <c>..</c> can never climb above.</summary>
    protected abstract int GetRootLength(string normalizedFullPath);

    /// <summary>
    /// Equality for the part of a path that decides which machine is contacted. Equal except for ASCII letter case;
    /// any non-ASCII character must match exactly, so no Unicode case pair can stand in for a different host.
    /// </summary>
    protected static bool NetworkRootEquals(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
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

    internal bool IsWithinNormalized(string path, string root)
    {
        var trimmedRoot = TrimTrailingSeparators(root);
        var trimmedPath = TrimTrailingSeparators(path);
        if (!trimmedPath.StartsWith(trimmedRoot, Comparison)
            || (trimmedPath.Length != trimmedRoot.Length && !IsDirectorySeparator(trimmedPath[trimmedRoot.Length])))
        {
            return false;
        }

        // For a network root the \\server\share part decides which host is contacted: require the strict comparison.
        var networkRoot = GetNetworkRoot(trimmedRoot);
        return networkRoot is null || NetworkRootEquals(trimmedPath.AsSpan(0, networkRoot.Length), networkRoot);
    }

    private static PathPolicy Detect()
    {
        if (OperatingSystem.IsWindows())
        {
            return Windows;
        }

        return OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst() ? MacOs : Posix;
    }
}
