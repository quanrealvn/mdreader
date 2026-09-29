using System.ComponentModel;
using MdReader.Core.Diagnostics;
using MdReader.Core.Threading;

namespace MdReader.Core.Documents;

/// <summary>
/// Watches one folder tree (ARCHITECTURE M13). A recursive <see cref="FileSystemWatcher"/> signals the same
/// <see cref="Debouncer"/> <see cref="DocumentWatcher"/> uses, so a burst of file-system events — a branch checkout,
/// an editor writing a temp file and renaming it, a folder being copied in — becomes one <see cref="Changed"/>.
/// </summary>
/// <remarks>
/// <para>It reports no detail on purpose. The pane's answer to any change is a re-scan, and a recursive watcher can
/// drop events (buffer overflow) or miss a move of the folder itself, so a "what exactly changed" API would be a
/// promise this can't keep.</para>
/// <para><b>Recovery.</b> An <c>Error</c> (buffer overflow) tears the watcher down, re-creates it and signals, so the
/// pane re-scans whatever it missed. A folder that is gone (or can't be watched at all) is polled for every
/// <see cref="FolderPollInterval"/>, and coming back signals too.</para>
/// <para><b>Threading.</b> <see cref="Changed"/> is raised on a thread-pool thread, never concurrently with itself.
/// <see cref="Dispose"/> is idempotent, never waits for file-system I/O, and — unless called from a handler — returns
/// only once no handler is running, so nothing is raised afterwards.</para>
/// </remarks>
internal sealed class FolderWatcher : IFolderWatcher
{
    /// <summary>How often a missing (or unwatchable) folder is checked for.</summary>
    internal static readonly TimeSpan FolderPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 64 KiB rather than the 8 KiB default: a recursive watcher over a large tree overflows the small buffer during
    /// something as ordinary as a checkout, and every overflow costs a full re-scan.
    /// </summary>
    internal const int BufferSize = 64 * 1024;

    internal const NotifyFilters WatchedChanges =
        NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size;

    private const string LogCategory = "FolderWatcher";

    private readonly IAppLog _log;
    private readonly TimeProvider _timeProvider;
    private readonly Debouncer _debouncer;

    private readonly Lock _workGate = new();    // serializes the file-system work; never taken by Dispose
    private readonly Lock _raiseGate = new();   // held while handlers run
    private readonly Lock _stateGate = new();   // guards the fields; never held during I/O or while handlers run

    // Guarded by _stateGate.
    private FileSystemWatcher? _watcher;
    private ITimer? _pollTimer;
    private bool _restartRequested;
    private bool _disposed;

    // Guarded by _workGate.
    private bool _startFailureLogged;

    public FolderWatcher(string folderPath, IAppLog log, TimeProvider timeProvider, TimeSpan debounceDelay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(timeProvider);

        FolderPath = Path.GetFullPath(folderPath);
        _log = log;
        _timeProvider = timeProvider;
        _debouncer = new Debouncer(debounceDelay, OnDebounced, timeProvider);

        lock (_workGate)
        {
            Restart();
        }
    }

    public string FolderPath { get; }

    public event EventHandler? Changed;

    /// <summary>True while the folder is missing (or can't be watched) and is polled for.</summary>
    internal bool IsPollingForFolder
    {
        get
        {
            lock (_stateGate)
            {
                return _pollTimer is not null;
            }
        }
    }

    public void Dispose()
    {
        FileSystemWatcher? watcher;
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
                pollTimer = _pollTimer;
                _watcher = null;
                _pollTimer = null;
            }
        }

        watcher?.Dispose();
        pollTimer?.Dispose();
        _debouncer.Dispose();
    }

    /// <summary>Test hook: behaves as if the current watcher raised <c>Error</c>.</summary>
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

    private void OnFileSystemEvent(object sender, FileSystemEventArgs e) => _debouncer.Signal();

    private void OnError(object sender, ErrorEventArgs e)
    {
        lock (_stateGate)
        {
            if (_disposed || !ReferenceEquals(sender, _watcher))
            {
                return;
            }

            _restartRequested = true;   // re-created by ProcessChange, not inside the watcher's own callback
        }

        _log.Write(AppLogLevel.Warning, LogCategory,
            $"Folder watcher error for '{FolderPath}'; recreating the watcher.", e.GetException());
        _debouncer.Signal();
    }

    /// <summary>Hands the (possibly slow) file-system work to the pool, so disposing the debouncer never waits for it.</summary>
    private void OnDebounced() =>
        ThreadPool.UnsafeQueueUserWorkItem(static watcher => watcher.ProcessChange(), this, preferLocal: false);

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
                // A folder that is gone starts the poll timer instead; the change is still worth reporting.
                Restart();
            }

            Raise();
        }
    }

    private void OnPoll(object? state)
    {
        lock (_workGate)
        {
            lock (_stateGate)
            {
                if (_disposed || _pollTimer is null)
                {
                    return;
                }
            }

            if (Restart())
            {
                _log.Write(AppLogLevel.Info, LogCategory, $"Watching '{FolderPath}' again.");
                _debouncer.Signal();
                return;
            }

            lock (_stateGate)
            {
                // One-shot timer, re-armed after each poll, so a slow poll (a hung share) can never overlap itself.
                if (!_disposed)
                {
                    _pollTimer?.Change(FolderPollInterval, Timeout.InfiniteTimeSpan);
                }
            }
        }
    }

    /// <summary>Replaces the watcher with a new one, or starts polling for the folder. Caller holds _workGate.</summary>
    /// <returns>True if a watcher is running afterwards.</returns>
    private bool Restart()
    {
        FileSystemWatcher? created = Directory.Exists(FolderPath) ? TryCreate() : null;

        FileSystemWatcher? old;
        ITimer? oldTimer = null;
        bool running = false;
        bool startedPolling = false;
        lock (_stateGate)
        {
            if (_disposed)
            {
                old = created;
            }
            else if (created is null)
            {
                old = _watcher;
                _watcher = null;
                startedPolling = StartPollLocked();
            }
            else
            {
                old = _watcher;
                oldTimer = _pollTimer;
                _watcher = created;
                _pollTimer = null;
                running = true;
            }
        }

        old?.Dispose();
        oldTimer?.Dispose();
        if (startedPolling)
        {
            _log.Write(AppLogLevel.Info, LogCategory,
                $"Folder '{FolderPath}' is missing or can't be watched; checking for it every "
                + $"{FolderPollInterval.TotalSeconds:0} s.");
        }

        return running;
    }

    private FileSystemWatcher? TryCreate()
    {
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(FolderPath)
            {
                IncludeSubdirectories = true,
                NotifyFilter = WatchedChanges,
                InternalBufferSize = BufferSize,
            };
            watcher.Created += OnFileSystemEvent;
            watcher.Deleted += OnFileSystemEvent;
            watcher.Renamed += OnFileSystemEvent;
            watcher.Changed += OnFileSystemEvent;
            watcher.Error += OnError;
            watcher.EnableRaisingEvents = true;
            _startFailureLogged = false;
            return watcher;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or Win32Exception
                                       or PlatformNotSupportedException)
        {
            watcher?.Dispose();
            if (!_startFailureLogged)
            {
                _startFailureLogged = true;
                _log.Write(AppLogLevel.Info, LogCategory, $"Can't watch '{FolderPath}' ({ex.Message}).");
            }

            return null;
        }
    }

    private void Raise()
    {
        lock (_raiseGate)
        {
            EventHandler? handler = Changed;
            if (handler is null)
            {
                return;
            }

            foreach (Delegate subscriber in handler.GetInvocationList())
            {
                if (IsDisposed())
                {
                    return;   // disposed by a previous subscriber
                }

                try
                {
                    ((EventHandler)subscriber)(this, EventArgs.Empty);
                }
                catch (Exception ex)
                {
                    // A throwing subscriber must neither starve the others nor take the process down.
                    _log.Write(AppLogLevel.Error, LogCategory, $"A Changed handler for '{FolderPath}' threw.", ex);
                }
            }
        }
    }

    /// <returns>True if polling was not active before.</returns>
    private bool StartPollLocked()
    {
        if (_pollTimer is not null)
        {
            return false;
        }

        _pollTimer = _timeProvider.CreateTimer(OnPoll, state: null, FolderPollInterval, Timeout.InfiniteTimeSpan);
        return true;
    }

    private bool IsDisposed()
    {
        lock (_stateGate)
        {
            return _disposed;
        }
    }
}
