using MdReader.Core.Diagnostics;

namespace MdReader.Core.Documents;

/// <summary>Creates <see cref="IDocumentWatcher"/>s (the only public entry point to <see cref="DocumentWatcher"/>).</summary>
public sealed class DocumentWatcherFactory : IDocumentWatcherFactory
{
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(200);

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
}
