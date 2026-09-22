namespace MdReader.Core.Paths;

/// <summary>
/// Chooses the folder mapped to https://doc.mdreader.example/ for a document (ARCHITECTURE §4.3): the nearest folder,
/// starting at the document's own folder, that contains a ".git" directory (repository) or file (worktree,
/// submodule); otherwise the document folder. At most <see cref="MaxAncestorLevels"/> folders are examined (the
/// document folder counts as the first), and the walk ends at the drive root or the \\server\share root, which is
/// examined too. The walk is lexical (<see cref="Path.GetDirectoryName(string)"/>), so it never leaves the
/// document's drive or share.
/// </summary>
public sealed class ResourceRootResolver
{
    public const int MaxAncestorLevels = 32;
    public const int MaxMappablePathLength = 240;   // WebView2 mapping requires < MAX_PATH; leave headroom

    private const string GitMarkerName = ".git";

    private readonly IFileSystemProbe _fileSystem;

    public ResourceRootResolver(IFileSystemProbe fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
    }

    /// Nearest ancestor (starting at the document folder) containing a ".git" file OR directory; else the document folder.
    /// Stops at the drive root / UNC share root. If the result is longer than MaxMappablePathLength, falls back to the document folder.
    public string Resolve(string documentPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentPath);

        var normalized = LocalPathResolver.NormalizeFullPath(documentPath);
        if (normalized is null)
        {
            // Not a drive-absolute or \\server\share path (e.g. \\?\…): don't probe anything we can't reason about.
            return Path.GetDirectoryName(documentPath) ?? documentPath;
        }

        var documentDirectory = Path.GetDirectoryName(normalized) ?? normalized;
        var directory = documentDirectory;
        for (var level = 0; level < MaxAncestorLevels && directory is not null; level++)
        {
            var marker = Path.Join(directory, GitMarkerName);
            if (_fileSystem.DirectoryExists(marker) || _fileSystem.FileExists(marker))
            {
                return directory.Length > MaxMappablePathLength ? documentDirectory : directory;
            }

            directory = Path.GetDirectoryName(directory);   // null above "C:\" and above "\\server\share"
        }

        return documentDirectory;
    }
}
