namespace MdReader.Core.Folders;

/// <summary>One entry of a scanned folder: a file, or a folder with its (already scanned) children.</summary>
public sealed record FolderNode(string FullPath, string Name, bool IsDirectory, IReadOnlyList<FolderNode> Children);

/// <summary>Why a folder couldn't be opened at all. A folder that is merely unreadable *inside* is not an error.</summary>
public enum FolderScanError
{
    NotFound,
    AccessDenied,
    IoError,
}

/// <summary>
/// The result of one scan. <see cref="Error"/> is set only when the root itself is unusable; everything else is
/// reported as counts, so a folder with a few unreadable corners still shows the parts that could be read.
/// </summary>
/// <param name="Files">Every file in the tree, sorted by full path (case-insensitively, then ordinal) so quick open
/// and search always see the same order for the same folder.</param>
/// <param name="SkippedDirectoryCount">Folders that couldn't be listed (permissions, a vanished folder, a bad link).</param>
/// <param name="Truncated">True when <see cref="FolderScanOptions.MaxEntries"/> or the depth cap stopped the walk.</param>
public sealed record FolderTree(
    string RootPath,
    string RootName,
    IReadOnlyList<FolderNode> Children,
    IReadOnlyList<string> Files,
    int SkippedDirectoryCount,
    bool Truncated,
    FolderScanError? Error)
{
    internal static FolderTree Failed(string rootPath, string rootName, FolderScanError error) =>
        new(rootPath, rootName, [], [], 0, false, error);
}
