namespace MdReader.Core.Paths;

/// <summary>
/// Asks the file system a yes/no question about a batch of paths without ever blocking the caller's thread, and
/// without letting one unreachable volume hold up the rest.
/// </summary>
/// <remarks>
/// <para>One dedicated worker per volume (a drive or <c>\\server\share</c> on Windows, a mount under
/// <c>/Volumes</c> or an automounted host on macOS) asks about its own paths in order. The workers are
/// <see cref="TaskCreationOptions.LongRunning"/> threads rather than pool work items on purpose: a routable but
/// silent UNC path takes the SMB stack about 20 s to give up on, and that must not cost a pool thread.</para>
/// <para>Paths that haven't answered when the timeout elapses are reported as <c>false</c> and their workers are
/// abandoned.</para>
/// </remarks>
internal static class FileSystemQuestion
{
    private const int Yes = 1, No = 2;   // 0 = no answer yet

    /// <summary>
    /// Asks <paramref name="ask"/> about every path, in the original order. Never throws for a path: a probe that
    /// throws counts as <c>false</c>.
    /// </summary>
    internal static async Task<bool[]> AskAsync(IReadOnlyList<string> paths, Func<string, bool> ask, TimeSpan timeout,
                                                TimeProvider? timeProvider)
    {
        var answers = new int[paths.Count];
        Task[] workers = Enumerable.Range(0, paths.Count)
            .GroupBy(i => RootOf(paths[i]), PathPolicy.Current.Comparer)
            .Select(group => Task.Factory.StartNew(() =>
            {
                foreach (int i in group)
                {
                    Volatile.Write(ref answers[i], Probe(ask, paths[i]) ? Yes : No);
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
               TaskScheduler.Default))
            .ToArray();

        Task all = Task.WhenAll(workers);
        using (var cancelDelay = new CancellationTokenSource())
        {
            Task delay = Task.Delay(timeout, timeProvider ?? TimeProvider.System, cancelDelay.Token);
            if (await Task.WhenAny(all, delay).ConfigureAwait(false) == all)
            {
                await cancelDelay.CancelAsync().ConfigureAwait(false);
            }
        }

        var result = new bool[paths.Count];
        for (var i = 0; i < paths.Count; i++)
        {
            result[i] = Volatile.Read(ref answers[i]) == Yes;
        }

        return result;
    }

    private static bool Probe(Func<string, bool> ask, string path)
    {
        try
        {
            return ask(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string RootOf(string path)
    {
        PathPolicy policy = PathPolicy.Current;
        return policy.GetVolumeRoot(policy.NormalizeFullPath(path)) ?? "";
    }
}

/// <summary>
/// Which of a batch of paths are folders (ARCHITECTURE M13). The shell asks this about every path that arrives
/// from outside — the command line, a drop, a second instance — and it must never be asked on the UI thread:
/// <c>Directory.Exists</c> on a routable but silent UNC path takes about 20 s to answer.
/// </summary>
public static class DirectoryProbe
{
    /// <summary>How long a path is given to answer before it is treated as "not a folder".</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// True at each index whose path names an existing folder. Never runs the probe on the caller's thread; a path
    /// that hasn't answered within <paramref name="timeout"/> is reported as not a folder, which opens it as a
    /// document instead — where the loader's own timeouts and error view take over.
    /// </summary>
    public static Task<bool[]> AreFoldersAsync(IReadOnlyList<string> paths, IFileSystemProbe probe, TimeSpan timeout,
                                               TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        return paths.Count == 0
            ? Task.FromResult<bool[]>([])
            : FileSystemQuestion.AskAsync(paths, probe.DirectoryExists, timeout, timeProvider);
    }
}
