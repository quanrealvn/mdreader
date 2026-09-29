namespace MdReader.Core.Folders;

/// <summary>
/// What <see cref="FolderScanner"/> walks and what it leaves out (ARCHITECTURE M13). Stateless and lexical: the
/// options themselves never touch the file system.
/// </summary>
public sealed record FolderScanOptions
{
    /// <summary>Most files a scan reports before it stops descending (a tree, not a file index).</summary>
    public const int DefaultMaxEntries = 20_000;

    /// <summary>Deepest folder level below the root that is still walked.</summary>
    public const int DefaultMaxDepth = 32;

    /// <summary>The ".gitignore-style" folders nobody reads notes from.</summary>
    public static IReadOnlyList<string> DefaultExcludedDirectories { get; } = ["node_modules", ".git", "bin", "obj"];

    internal static FolderScanOptions Default { get; } = new();

    /// <summary>
    /// False (the default) shows only the files MdReader opens — <see cref="Paths.DocumentFileTypes"/>, Markdown and
    /// JSON — and prunes folders that end up with nothing in them.
    /// </summary>
    public bool IncludeAllFiles { get; init; }

    /// <summary>Folder names skipped wherever they appear, matched the way the platform matches file names.</summary>
    public IReadOnlyCollection<string> ExcludedDirectories { get; init; } = DefaultExcludedDirectories;

    public int MaxEntries { get; init; } = DefaultMaxEntries;

    public int MaxDepth { get; init; } = DefaultMaxDepth;
}
