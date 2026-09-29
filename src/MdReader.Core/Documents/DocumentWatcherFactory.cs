using MdReader.Core.Diagnostics;

namespace MdReader.Core.Documents;

/// <summary>
/// Creates the file-system watchers (the only public entry point to <see cref="DocumentWatcher"/> and
/// <see cref="FolderWatcher"/>). One factory for both, so the whole app shares one log and one
/// <see cref="TimeProvider"/> — which is also what lets a test drive every watcher off one fake clock.
/// </summary>
public sealed class DocumentWatcherFactory : IDocumentWatcherFactory, IFolderWatcherFactory
{
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Longer than <see cref="DebounceDelay"/>: a folder change costs a re-scan of the whole tree, and the events
    /// that matter (a checkout, a copy, an editor's temp-file dance) arrive in bursts worth waiting out.
    /// </summary>
    public static readonly TimeSpan FolderDebounceDelay = TimeSpan.FromMilliseconds(400);

    private readonly IAppLog _log;
    private readonly TimeProvider _timeProvider;

    public DocumentWatcherFactory(IAppLog log, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(log);

        _log = log;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Starts watching <paramref name="documentPath"/>. The file doesn't need to exist; if its folder is missing, the
    /// watcher polls for it every 2 s.
    /// </summary>
    /// <remarks>
    /// <b>Blocking:</b> this performs synchronous directory I/O (it checks the parent and grandparent folders and opens
    /// handles on them), which can block for a long time on an unreachable network share. Call it off the UI thread.
    /// Disposing the returned watcher never waits for file-system I/O.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="documentPath"/> is empty or has no file name.</exception>
    public IDocumentWatcher Create(string documentPath) => new DocumentWatcher(documentPath, _log, _timeProvider, DebounceDelay);

    /// <summary>
    /// Starts watching <paramref name="folderPath"/> and everything below it. The folder doesn't need to exist; a
    /// missing one is polled for every 2 s.
    /// </summary>
    /// <remarks><b>Blocking:</b> opens a handle on the folder, so call it off the UI thread (a network share can take
    /// seconds). Disposing the returned watcher never waits for file-system I/O.</remarks>
    /// <exception cref="ArgumentException"><paramref name="folderPath"/> is empty or whitespace.</exception>
    IFolderWatcher IFolderWatcherFactory.Create(string folderPath) => CreateFolderWatcher(folderPath);

    /// <inheritdoc cref="IFolderWatcherFactory.Create"/>
    public IFolderWatcher CreateFolderWatcher(string folderPath) =>
        new FolderWatcher(folderPath, _log, _timeProvider, FolderDebounceDelay);
}
