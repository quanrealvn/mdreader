using System.ComponentModel;
using MdReader.Core.Diagnostics;
using MdReader.Core.Threading;

namespace MdReader.Core.Documents;

/// <summary>
/// Watches one document (ARCHITECTURE §4.4). A <see cref="FileSystemWatcher"/> on the parent folder, filtered to the
/// file name, signals a <see cref="Debouncer"/>. When the debounce fires, the event kind is decided by the file's
/// current state, not by the raw events, so every save strategy (in-place write, temp file + rename, delete + rename)
/// ends in exactly one <see cref="DocumentChangeKind.Changed"/>. <see cref="DocumentChangeKind.Deleted"/> is raised
/// once until the file exists again.
/// </summary>
/// <remarks>
/// <para><b>Parent folder moves.</b> A watcher's handle follows its folder when the folder is renamed or moved (Explorer's
/// Delete moves it to the Recycle Bin), and nothing inside the folder reports that. So a second watcher on the
/// grandparent folder, filtered to the parent folder's name, reports the parent being removed, renamed or created.
/// Each such event tears both watchers down and re-creates them on the original path (or polls for the folder every
/// 2 s while it is missing); then the file's state decides the event as usual. Edits to a moved copy are therefore
/// never reported for the original path. Moves of folders further up are only noticed at the next event (limitation).</para>
/// <para><b>Threading.</b> <see cref="Changed"/> is raised on a thread-pool thread and never concurrently with itself.
/// <see cref="Dispose"/> is idempotent and never waits for file-system I/O (a hung network share can't freeze the
/// caller). It only waits for a <see cref="Changed"/> handler that is running at that moment, unless called from that
/// handler, so no event is raised after it returns. Handlers must therefore not block on the thread that disposes the
/// watcher (post to the dispatcher asynchronously instead).</para>
/// <para><b>Locks</b>, in acquisition order: <c>_workGate</c> (serializes the work that touches the file system:
/// re-creating the watchers, polling for the folder, deciding and raising events; held during I/O, never taken by
/// Dispose) → <c>_raiseGate</c> (held while handlers run; Dispose takes it to set the disposed flag) →
/// <c>_stateGate</c> (guards the fields; never held during I/O or while handlers run).</para>
/// </remarks>
internal sealed class DocumentWatcher : IDocumentWatcher
{
    /// <summary>How often a missing (or unwatchable) parent folder is checked for.</summary>
    internal static readonly TimeSpan FolderPollInterval = TimeSpan.FromSeconds(2);

    internal const NotifyFilters WatchedChanges =
        NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime;

    private const string LogCategory = "DocumentWatcher";

    private readonly string _directory;
    private readonly string _fileName;
    private readonly string? _grandparentDirectory;   // null when the parent folder is a drive or share root
    private readonly string _parentFolderName;
    private readonly IAppLog _log;
    private readonly TimeProvider _timeProvider;
    private readonly Debouncer _debouncer;

    private readonly Lock _workGate = new();
    private readonly Lock _raiseGate = new();
    private readonly Lock _stateGate = new();

    // Guarded by _stateGate.
    private FileSystemWatcher? _watcher;         // parent folder, filtered to the file name
    private FileSystemWatcher? _parentWatcher;   // grandparent folder, filtered to the parent folder's name
    private ITimer? _folderPollTimer;
    private bool _restartRequested;
    private bool _deletedRaised;
    private bool _disposed;

    // Guarded by _workGate.
    private bool _startFailureLogged;

    public DocumentWatcher(string documentPath, IAppLog log, TimeProvider timeProvider, TimeSpan debounceDelay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentPath);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(timeProvider);

        DocumentPath = Path.GetFullPath(documentPath);
        _fileName = Path.GetFileName(DocumentPath);
        _directory = Path.GetDirectoryName(DocumentPath) ?? string.Empty;
        if (_fileName.Length == 0 || _directory.Length == 0)
        {
            throw new ArgumentException($"'{documentPath}' is not a file path.", nameof(documentPath));
        }

        _parentFolderName = Path.GetFileName(_directory);
        _grandparentDirectory = _parentFolderName.Length == 0 ? null : Path.GetDirectoryName(_directory);

        _log = log;
        _timeProvider = timeProvider;
        _debouncer = new Debouncer(debounceDelay, OnDebounced, timeProvider);

        lock (_workGate)
        {
            RestartWatchers();
        }
    }

    public string DocumentPath { get; }

    public event EventHandler<DocumentChangedEventArgs>? Changed;

    /// <summary>True while the parent folder is missing (or can't be watched) and is polled for.</summary>
    internal bool IsPollingForFolder
    {
        get
        {
            lock (_stateGate)
            {
                return _folderPollTimer is not null;
            }
        }
    }

    /// <summary>True while the grandparent watcher (which reports moves of the parent folder) is active.</summary>
    internal bool IsWatchingParentFolder
    {
        get
        {
            lock (_stateGate)
            {
                return _parentWatcher is not null;
            }
        }
    }

    public void Dispose()
    {
        FileSystemWatcher? watcher;
        FileSystemWatcher? parentWatcher;
        ITimer? pollTimer;
        lock (_raiseGate)
        {
            lock (_stateGate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                watcher = _watcher;
                parentWatcher = _parentWatcher;
                pollTimer = _folderPollTimer;
                _watcher = null;
                _parentWatcher = null;
                _folderPollTimer = null;
            }
        }

        watcher?.Dispose();
        parentWatcher?.Dispose();
        pollTimer?.Dispose();

        // Quick: the debounced callback only queues work.
        _debouncer.Dispose();
    }

    /// <summary>Test hook: behaves as if the current file watcher raised <c>Error</c>.</summary>
    internal void SimulateWatcherError(Exception exception)
    {
        FileSystemWatcher? current;
        lock (_stateGate)
        {
            current = _watcher;
        }

        if (current is not null)
        {
            OnError(current, new ErrorEventArgs(exception));
        }
    }

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        if (IsTarget(e.Name))
        {
            _debouncer.Signal();
        }
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (IsTarget(e.Name) || IsTarget(e.OldName))
        {
            _debouncer.Signal();
        }
    }

    /// <summary>Grandparent watcher: the parent folder was removed, renamed (away or back) or created.</summary>
    private void OnParentFolderEvent(object sender, FileSystemEventArgs e)
    {
        if (!IsParentFolder(e.Name) && !(e is RenamedEventArgs renamed && IsParentFolder(renamed.OldName)))
        {
            return;
        }

        lock (_stateGate)
        {
            if (_disposed || !ReferenceEquals(sender, _parentWatcher))
            {
                return;
            }

            // The file watcher may now follow a moved folder, or the original path may now be a different folder:
            // re-create both watchers on the original path.
            _restartRequested = true;
        }

        _debouncer.Signal();
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        lock (_stateGate)
        {
            if (_disposed || !(ReferenceEquals(sender, _watcher) || ReferenceEquals(sender, _parentWatcher)))
            {
                return;
            }

            // Re-created by ProcessChange, not inside the watcher's own callback.
            _restartRequested = true;
        }

        _log.Write(AppLogLevel.Warning, LogCategory,
            $"File watcher error for '{DocumentPath}'; recreating the watcher.", e.GetException());
        _debouncer.Signal();
    }

    /// <summary>Debouncer callback. Hands the (possibly slow) file-system work to the thread pool so that disposing
    /// the debouncer never waits for it.</summary>
    private void OnDebounced() =>
        ThreadPool.UnsafeQueueUserWorkItem(static watcher => watcher.ProcessChange(), this, preferLocal: false);

    /// <summary>Re-creates the watchers if requested, decides the event kind from the file's state and raises it.</summary>
    private void ProcessChange()
    {
        lock (_workGate)
        {
            bool restart;
            lock (_stateGate)
            {
                if (_disposed)
                {
                    return;
                }

                restart = _restartRequested;
                _restartRequested = false;
            }

            if (restart)
            {
                RestartWatchers();
            }

            var exists = File.Exists(DocumentPath);
            var folderGone = !exists && !Directory.Exists(_directory);

            DocumentChangeKind? kind;
            FileSystemWatcher? stale = null;
            var startedPolling = false;
            lock (_stateGate)
            {
                if (_disposed)
                {
                    return;
                }

                if (exists)
                {
                    _deletedRaised = false;
                    kind = DocumentChangeKind.Changed;
                }
                else
                {
                    // A folder removal further up isn't reported, and a watcher on a removed folder would never see
                    // the file come back: switch to polling for the folder.
                    if (folderGone && _watcher is not null)
                    {
                        stale = _watcher;
                        _watcher = null;
                        startedPolling = StartFolderPollLocked();
                    }

                    kind = _deletedRaised ? null : DocumentChangeKind.Deleted;
                    _deletedRaised = true;
                }
            }

            stale?.Dispose();
            if (startedPolling)
            {
                LogPollingStarted();
            }

            if (kind is { } raised)
            {
                Raise(raised);
            }
        }
    }

    private void OnFolderPoll(object? state)
    {
        lock (_workGate)
        {
            lock (_stateGate)
            {
                if (_disposed || _folderPollTimer is null)
                {
                    return;
                }
            }

            if (RestartWatchers())
            {
                _log.Write(AppLogLevel.Info, LogCategory, $"Watching '{_directory}' again.");
                _debouncer.Signal();
                return;
            }

            lock (_stateGate)
            {
                // One-shot timer re-armed after each poll, so slow polls (e.g. a hung network share) never overlap.
                if (!_disposed)
                {
                    _folderPollTimer?.Change(FolderPollInterval, Timeout.InfiniteTimeSpan);
                }
            }
        }
    }

    /// <summary>
    /// Replaces both watchers with new ones on the original paths. If the parent folder is missing or can't be
    /// watched, the file watcher is torn down and the folder is polled for; the grandparent watcher is then kept (it
    /// may report the folder coming back before the next poll). Caller holds _workGate. Does file-system I/O.
    /// </summary>
    /// <returns>True if the file watcher is running afterwards.</returns>
    private bool RestartWatchers()
    {
        var watcher = Directory.Exists(_directory) ? TryCreateFileWatcher() : null;
        var parentWatcher = watcher is not null ? TryCreateParentWatcher() : null;

        FileSystemWatcher? oldWatcher = null;
        FileSystemWatcher? oldParentWatcher = null;
        ITimer? oldPollTimer = null;
        var running = false;
        var startedPolling = false;
        lock (_stateGate)
        {
            if (_disposed)
            {
                oldWatcher = watcher;
                oldParentWatcher = parentWatcher;
            }
            else if (watcher is null)
            {
                oldWatcher = _watcher;
                _watcher = null;
                startedPolling = StartFolderPollLocked();
            }
            else
            {
                oldWatcher = _watcher;
                oldParentWatcher = _parentWatcher;
                oldPollTimer = _folderPollTimer;
                _watcher = watcher;
                _parentWatcher = parentWatcher;
                _folderPollTimer = null;
                running = true;
            }
        }

        oldWatcher?.Dispose();
        oldParentWatcher?.Dispose();
        oldPollTimer?.Dispose();
        if (startedPolling)
        {
            LogPollingStarted();
        }

        return running;
    }

    private void Raise(DocumentChangeKind kind)
    {
        lock (_raiseGate)
        {
            var handler = Changed;
            if (handler is null)
            {
                return;
            }

            var args = new DocumentChangedEventArgs(kind);
            foreach (var subscriber in handler.GetInvocationList())
            {
                if (IsDisposed())
                {
                    return;   // disposed by a previous subscriber
                }

                try
                {
                    ((EventHandler<DocumentChangedEventArgs>)subscriber)(this, args);
                }
                catch (Exception ex)
                {
                    // A throwing subscriber must neither starve the others nor take down the thread (and the process).
                    _log.Write(AppLogLevel.Error, LogCategory, $"A Changed handler for '{DocumentPath}' threw.", ex);
                }
            }
        }
    }

    /// <summary>Creates and starts the file watcher, or returns null (logged once per failure streak).</summary>
    private FileSystemWatcher? TryCreateFileWatcher()
    {
        var watcher = TryCreate(_directory, _fileName, WatchedChanges, w =>
        {
            w.Changed += OnFileSystemEvent;
            w.Created += OnFileSystemEvent;
            w.Deleted += OnFileSystemEvent;
            w.Renamed += OnRenamed;
        }, out var error);

        if (watcher is not null)
        {
            _startFailureLogged = false;
            return watcher;
        }

        if (!_startFailureLogged)
        {
            _startFailureLogged = true;
            _log.Write(AppLogLevel.Info, LogCategory,
                $"Can't watch '{_directory}' ({error}); checking again every {FolderPollInterval.TotalSeconds:0} s.");
        }

        return null;
    }

    /// <summary>Creates the grandparent watcher, or returns null when there is no grandparent or it can't be watched
    /// (moves of the parent folder are then only noticed at the next event).</summary>
    private FileSystemWatcher? TryCreateParentWatcher()
    {
        if (_grandparentDirectory is null)
        {
            return null;
        }

        var watcher = TryCreate(_grandparentDirectory, _parentFolderName, NotifyFilters.DirectoryName, w =>
        {
            w.Created += OnParentFolderEvent;
            w.Deleted += OnParentFolderEvent;
            w.Renamed += OnParentFolderEvent;
        }, out var error);

        if (watcher is null)
        {
            _log.Write(AppLogLevel.Debug, LogCategory,
                $"Can't watch '{_grandparentDirectory}' for moves of '{_directory}' ({error}).");
        }

        return watcher;
    }

    /// <summary>Creates a non-recursive watcher, subscribes its handlers, then starts it.</summary>
    private FileSystemWatcher? TryCreate(string directory, string filter, NotifyFilters notifyFilter,
        Action<FileSystemWatcher> subscribe, out string? error)
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(directory, filter)
            {
                IncludeSubdirectories = false,
                NotifyFilter = notifyFilter,
            };
            subscribe(watcher);
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;
            error = null;
            return watcher;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or Win32Exception
                                       or PlatformNotSupportedException)
        {
            watcher?.Dispose();
            error = ex.Message;
            return null;
        }
    }

    /// <returns>True if polling was not active before.</returns>
    private bool StartFolderPollLocked()
    {
        if (_folderPollTimer is not null)
        {
            return false;
        }

        _folderPollTimer = _timeProvider.CreateTimer(OnFolderPoll, state: null, FolderPollInterval, Timeout.InfiniteTimeSpan);
        return true;
    }

    private void LogPollingStarted() =>
        _log.Write(AppLogLevel.Info, LogCategory,
            $"Folder '{_directory}' is missing or can't be watched; checking for it every {FolderPollInterval.TotalSeconds:0} s.");

    private bool IsDisposed()
    {
        lock (_stateGate)
        {
            return _disposed;
        }
    }

    private bool IsTarget(string? name) => string.Equals(name, _fileName, StringComparison.OrdinalIgnoreCase);

    private bool IsParentFolder(string? name) => string.Equals(name, _parentFolderName, StringComparison.OrdinalIgnoreCase);
}
