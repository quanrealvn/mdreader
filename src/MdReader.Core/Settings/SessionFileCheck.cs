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

    private const int Exists = 1, Missing = 2;   // 0 = no answer yet

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

        var answers = new int[files.Count];
        var workers = Enumerable.Range(0, files.Count)
            .GroupBy(i => RootOf(files[i]), PathPolicy.Current.Comparer)
            .Select(group => Task.Factory.StartNew(() =>
            {
                foreach (var i in group)
                {
                    Volatile.Write(ref answers[i], ProbeExists(probe, files[i]) ? Exists : Missing);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach, TaskScheduler.Default))
            .ToArray();

        var all = Task.WhenAll(workers);
        using (var cancelDelay = new CancellationTokenSource())
        {
            var delay = Task.Delay(timeout, timeProvider ?? TimeProvider.System, cancelDelay.Token);
            if (await Task.WhenAny(all, delay).ConfigureAwait(false) == all)
            {
                await cancelDelay.CancelAsync().ConfigureAwait(false);
            }
        }

        var existing = new List<string>(files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            if (Volatile.Read(ref answers[i]) == Exists)
            {
                existing.Add(files[i]);
            }
        }

        return existing;
    }

    private static bool ProbeExists(IFileSystemProbe probe, string path)
    {
        try
        {
            return probe.FileExists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string RootOf(string path)
    {
        var policy = PathPolicy.Current;
        return policy.GetVolumeRoot(policy.NormalizeFullPath(path)) ?? "";
    }
}
