namespace MdReader.Core.Paths;

/// <summary>
/// Chooses the folder mapped to https://doc.mdreader.example/ for a document (ARCHITECTURE §4.3): the nearest folder,
/// starting at the document's own folder, that contains a ".git" directory (repository) or file (worktree,
/// submodule); otherwise the document folder. At most <see cref="MaxAncestorLevels"/> folders are examined (the
/// document folder counts as the first), and the walk ends at the volume root, which is examined too. The walk is
/// lexical, so it never leaves the document's volume: on Windows that is the drive or the \\server\share root, on
/// macOS "/" or the mount point under /Volumes, on Linux "/".
/// </summary>
public sealed class ResourceRootResolver
{
    public const int MaxAncestorLevels = 32;

    /// <summary>WebView2's virtual-host mapping requires &lt; MAX_PATH; leave headroom. Other platforms have no such
    /// limit, so the effective cap comes from <see cref="PathPolicy.MaxMappableRootLength"/>.</summary>
    public const int MaxMappablePathLength = 240;

    private const string GitMarkerName = ".git";

    private readonly IFileSystemProbe _fileSystem;
    private readonly PathPolicy _policy;

    public ResourceRootResolver(IFileSystemProbe fileSystem, PathPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
        _policy = policy ?? PathPolicy.Current;
    }

    /// Nearest ancestor (starting at the document folder) containing a ".git" file OR directory; else the document folder.
    /// Stops at the volume root. If the result is longer than the platform's mappable length, falls back to the document folder.
    public string Resolve(string documentPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentPath);

        var normalized = _policy.NormalizeFullPath(documentPath);
        if (normalized is null)
        {
            // Not a path we can reason about (e.g. \\?\… on Windows): don't probe anything.
            return _policy.GetDirectoryName(documentPath) ?? documentPath;
        }

        var documentDirectory = _policy.GetDirectoryName(normalized) ?? normalized;
        var volumeRoot = _policy.GetVolumeRoot(documentDirectory);
        var directory = documentDirectory;
        for (var level = 0; level < MaxAncestorLevels && directory is not null; level++)
        {
            var marker = _policy.Join(directory, GitMarkerName);
            if (_fileSystem.DirectoryExists(marker) || _fileSystem.FileExists(marker))
            {
                return directory.Length > _policy.MaxMappableRootLength ? documentDirectory : directory;
            }

            // The volume root is examined, never climbed past: one ".git" above a mount point must not map the disk.
            directory = volumeRoot is not null && _policy.PathEquals(directory, volumeRoot)
                ? null
                : _policy.GetDirectoryName(directory);
        }

        return documentDirectory;
    }
}
