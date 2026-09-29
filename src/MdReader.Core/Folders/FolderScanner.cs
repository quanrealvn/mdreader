using MdReader.Core.Paths;

namespace MdReader.Core.Folders;

/// <summary>
/// Walks a folder into the immutable <see cref="FolderTree"/> the shell's file pane shows (ARCHITECTURE M13).
/// </summary>
/// <remarks>
/// <para>The walk is iterative: a notes folder can be nested far deeper than a recursive walk's stack survives, and
/// <see cref="FolderScanOptions.MaxDepth"/> is a policy, not a crash guard.</para>
/// <para>It never throws for anything the file system does. A root that can't be opened comes back as
/// <see cref="FolderTree.Error"/>; a folder inside that can't be listed (permissions, a folder deleted while the
/// scan runs, a path that got too long) is skipped and counted in <see cref="FolderTree.SkippedDirectoryCount"/>,
/// so one unreadable corner never costs the user the rest of the tree.</para>
/// <para><b>Links.</b> A folder that is a symbolic link or a junction is followed only once and only when its final
/// target isn't the folder itself or one of its ancestors, so <c>a/b -> a</c> can't spin forever and two links to
/// the same tree can't double it.</para>
/// <para>Ordering is lexical and culture-independent (folders first, then files, each case-insensitive then ordinal),
/// so the same folder always produces the same tree on every machine.</para>
/// </remarks>
public static class FolderScanner
{
    /// <summary>
    /// Scans <paramref name="folderPath"/>. Blocking and I/O bound: call it from a thread-pool thread.
    /// </summary>
    /// <exception cref="OperationCanceledException">The scan was cancelled.</exception>
    public static FolderTree Scan(string folderPath, FolderScanOptions? options = null,
                                  CancellationToken cancellationToken = default)
    {
        options ??= FolderScanOptions.Default;
        PathPolicy policy = PathPolicy.Current;

        string? rootPath;
        try
        {
            rootPath = policy.NormalizeFullPath(folderPath) ?? Path.GetFullPath(folderPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException
                                       or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return FolderTree.Failed(folderPath ?? "", policy.GetFileName(folderPath), FolderScanError.NotFound);
        }

        rootPath = policy.TrimTrailingSeparators(rootPath);
        string rootName = policy.GetFileName(rootPath);
        if (rootName.Length == 0)
        {
            rootName = rootPath;   // a volume root ("C:\", "/") has no file name of its own
        }

        // No Directory.Exists pre-check: it answers false both for a folder that isn't there and for one this
        // account may not read, and those must not look the same. Listing the root is the one call that can tell
        // them apart, so the walk does it and reports what the file system said.
        var state = new ScanState(options, policy, rootPath);
        try
        {
            FolderNode root = Walk(rootPath, rootName, state, cancellationToken, out FolderScanError? rootFailure);
            if (rootFailure is { } failure)
            {
                return FolderTree.Failed(rootPath, rootName, failure);
            }

            state.Files.Sort(CompareNames);   // the walk's order is depth-first; quick open and search want it stable
            return new FolderTree(rootPath, rootName, root.Children, state.Files, state.SkippedDirectories,
                                  state.Truncated, Error: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return FolderTree.Failed(rootPath, rootName, FolderScanError.AccessDenied);
        }
        catch (Exception ex) when (ex is IOException or System.Security.SecurityException)
        {
            return FolderTree.Failed(rootPath, rootName, FolderScanError.IoError);
        }
    }

    /// <summary>Depth-first walk with an explicit stack; each frame is completed into an immutable node on the way out.</summary>
    /// <param name="rootFailure">Why the root itself couldn't be listed, or null when it could.</param>
    private static FolderNode Walk(string rootPath, string rootName, ScanState state, CancellationToken cancellationToken,
                                   out FolderScanError? rootFailure)
    {
        var stack = new Stack<Frame>();
        Frame rootFrame = CreateFrame(rootPath, rootName, depth: 0, state, cancellationToken);

        // The root is the one folder whose listing failure is the whole answer rather than a skipped corner.
        rootFailure = rootFrame.Failure;
        if (rootFailure is not null)
        {
            state.SkippedDirectories--;   // reported as the tree's Error instead
            return rootFrame.ToNode();
        }

        stack.Push(rootFrame);

        FolderNode? completed = null;
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Frame frame = stack.Peek();

            if (completed is not null)
            {
                // The child we descended into is finished: keep it unless it is an empty branch we prune away.
                if (completed.Children.Count > 0 || state.Options.IncludeAllFiles)
                {
                    frame.Directories.Add(completed);
                }

                completed = null;
            }

            if (frame.NextChild < frame.ChildDirectories.Count)
            {
                // Always descend. A frame that is too deep, or that arrives after the entry cap, comes back empty
                // from CreateFrame without listing anything — one deep branch must not cut off its siblings.
                (string path, string name) = frame.ChildDirectories[frame.NextChild++];
                stack.Push(CreateFrame(path, name, frame.Depth + 1, state, cancellationToken));
                continue;
            }

            stack.Pop();
            completed = frame.ToNode();
        }

        return completed!;
    }

    /// <summary>Lists one folder and decides which of its subfolders are worth descending into.</summary>
    private static Frame CreateFrame(string path, string name, int depth, ScanState state, CancellationToken cancellationToken)
    {
        var frame = new Frame(path, name, depth);
        if (state.Entries >= state.Options.MaxEntries)
        {
            // Whoever reached the cap already reported it; a folder queued before that isn't news of its own.
            return frame;
        }

        List<FileSystemInfo>? entries = TryList(path, state, cancellationToken, out FolderScanError? failure);
        frame.Failure = failure;
        if (entries is null)
        {
            return frame;
        }

        if (depth >= state.Options.MaxDepth)
        {
            // Listing it first is what makes "truncated" honest: a folder at the cap that holds nothing was not
            // something we left out.
            state.Truncated |= entries.Count > 0;
            return frame;
        }

        entries.Sort(CompareEntries);
        foreach (FileSystemInfo entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.Entries >= state.Options.MaxEntries)
            {
                state.Truncated = true;
                break;
            }

            if (entry is DirectoryInfo directory)
            {
                if (state.IsExcluded(directory.Name) || !state.CanDescend(directory, path))
                {
                    continue;
                }

                frame.ChildDirectories.Add((directory.FullName, directory.Name));
                state.Entries++;
            }
            // The pane lists what can be opened from it, so the test is "does this application render it?" rather
            // than "is it Markdown?". A JSON file that renders in a tab but never appears in the tree could only be
            // reached by going back to Explorer, which is the trip the pane exists to save.
            else if (state.Options.IncludeAllFiles || DocumentFileTypes.IsDocumentPath(entry.Name))
            {
                frame.Files.Add(new FolderNode(entry.FullName, entry.Name, IsDirectory: false, []));
                state.Files.Add(entry.FullName);
                state.Entries++;
            }
        }

        return frame;
    }

    /// <summary>Everything in one folder, or null when it can't be listed (counted as skipped, and said why).</summary>
    /// <remarks>
    /// <c>IgnoreInaccessible</c> is deliberately <b>false</b>. With it on, a folder this account may not read comes
    /// back as zero entries and no exception — indistinguishable from an empty folder, so it would be pruned from
    /// the tree, left out of the skipped count, and (as the root) recorded in the settings as a folder worth
    /// reopening. The enumeration is not recursive, so the only thing the flag could have hidden is the failure to
    /// open this one folder, which is exactly what has to be reported.
    /// </remarks>
    private static List<FileSystemInfo>? TryList(string path, ScanState state, CancellationToken cancellationToken,
                                                 out FolderScanError? failure)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            IgnoreInaccessible = false,
            // "Show all files" also reveals the hidden ones; the default view is notes, not dot-directories.
            AttributesToSkip = state.Options.IncludeAllFiles
                ? FileAttributes.System
                : FileAttributes.Hidden | FileAttributes.System,
        };

        var entries = new List<FileSystemInfo>();
        try
        {
            // Enumeration is lazy, so a folder that can't be opened throws here rather than at the call above.
            foreach (FileSystemInfo entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries.Add(entry);
            }

            failure = null;
            return entries;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            failure = ex switch
            {
                UnauthorizedAccessException or System.Security.SecurityException => FolderScanError.AccessDenied,
                DirectoryNotFoundException => FolderScanError.NotFound,
                IOException or ArgumentException or NotSupportedException => FolderScanError.IoError,
                _ => null,
            };

            if (failure is null)
            {
                throw;
            }

            state.SkippedDirectories++;
            return null;
        }
    }

    /// <summary>Folders before files, then by name: case-insensitively first so "Notes" and "notes" stay together.</summary>
    private static int CompareEntries(FileSystemInfo left, FileSystemInfo right)
    {
        bool leftIsDirectory = left is DirectoryInfo;
        if (leftIsDirectory != right is DirectoryInfo)
        {
            return leftIsDirectory ? -1 : 1;
        }

        return CompareNames(left.Name, right.Name);
    }

    /// <summary>Case-insensitive first (so "Notes" and "notes" stay together), ordinal to break the tie.</summary>
    private static int CompareNames(string left, string right)
    {
        int byName = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        return byName != 0 ? byName : string.CompareOrdinal(left, right);
    }

    /// <summary>Mutable counters shared by the whole walk.</summary>
    private sealed class ScanState(FolderScanOptions options, PathPolicy policy, string rootPath)
    {
        private readonly HashSet<string> _excluded = new(options.ExcludedDirectories, policy.Comparer);
        private readonly HashSet<string> _followedLinks = new(policy.Comparer);

        public FolderScanOptions Options { get; } = options;

        public List<string> Files { get; } = [];

        public int Entries { get; set; }

        public int SkippedDirectories { get; set; }

        public bool Truncated { get; set; }

        public bool IsExcluded(string name) => _excluded.Contains(name);

        /// <summary>
        /// True unless <paramref name="directory"/> is a link that would take the walk back into a folder it is
        /// already inside, or into a target some other link already led to.
        /// </summary>
        public bool CanDescend(DirectoryInfo directory, string parentPath)
        {
            string? linkTarget;
            try
            {
                if (directory.LinkTarget is null)
                {
                    return true;
                }

                linkTarget = directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                SkippedDirectories++;
                return false;   // a broken or unreadable link: nothing to walk
            }

            if (linkTarget is null)
            {
                SkippedDirectories++;
                return false;
            }

            // Pointing at the folder it sits in, or at any ancestor of it, is the loop we must not walk into.
            if (policy.IsWithin(parentPath, linkTarget) || policy.PathEquals(directory.FullName, linkTarget))
            {
                SkippedDirectories++;
                return false;
            }

            // Pointing anywhere else inside the folder being scanned: the walk reaches those files on its own, and
            // following the link as well would list them twice — twice in the tree, twice in quick open, and read
            // twice by a search.
            if (policy.IsWithin(linkTarget, rootPath))
            {
                SkippedDirectories++;
                return false;
            }

            if (!_followedLinks.Add(linkTarget))
            {
                SkippedDirectories++;   // a second link to the same tree: show it once
                return false;
            }

            return true;
        }
    }

    /// <summary>One folder being walked: its files (already final) and the subfolders still to descend into.</summary>
    private sealed class Frame(string path, string name, int depth)
    {
        public int Depth { get; } = depth;

        /// <summary>Why this folder couldn't be listed, or null. Only the root's answer becomes the tree's Error.</summary>
        public FolderScanError? Failure { get; set; }

        public List<(string Path, string Name)> ChildDirectories { get; } = [];

        public List<FolderNode> Directories { get; } = [];

        public List<FolderNode> Files { get; } = [];

        public int NextChild { get; set; }

        public FolderNode ToNode()
        {
            var children = new List<FolderNode>(Directories.Count + Files.Count);
            children.AddRange(Directories);
            children.AddRange(Files);
            return new FolderNode(path, name, IsDirectory: true, children);
        }
    }
}
