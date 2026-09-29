using MdReader.Core.Paths;

namespace MdReader.Core.Settings;

/// Startup session restore (ARCHITECTURE §4.6, §6): which of the saved session's files still exist. The checks never run
/// on the caller's thread: one dedicated worker per volume (a drive or \\server\share on Windows, a mount under
/// /Volumes or an automounted host on macOS) checks its paths in order, so a hung network share or disconnected drive
/// blocks only its own worker (and never a thread-pool thread). Paths that haven't answered when the timeout elapses
/// count as missing; their workers are abandoned.
public static class SessionFileCheck
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    /// The files that exist, in their original order. Never throws for a path (a probe failure counts as missing).
    public static async Task<IReadOnlyList<string>> FilterExistingAsync(IReadOnlyList<string> files, IFileSystemProbe probe,
                                                                        TimeSpan timeout, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        if (files.Count == 0)
        {
            return [];
        }

        // The worker-per-volume machinery is shared with DirectoryProbe (M13), which asks the same kind of question
        // about folders and needs the same guarantee that a dead share can't hold anything up.
        bool[] answers = await FileSystemQuestion.AskAsync(files, probe.FileExists, timeout, timeProvider).ConfigureAwait(false);

        var existing = new List<string>(files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            if (answers[i])
            {
                existing.Add(files[i]);
            }
        }

        return existing;
    }
}
