using MdReader.Core.Diagnostics;
using MdReader.Core.Threading;

namespace MdReader.Core.Settings;

// Owned by WP4 (ARCHITECTURE §2.2, §4.6).
// Single owner of the live settings (App registers as singleton).
public sealed class SettingsCoordinator : IDisposable
{
    public static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly ISettingsStore _store;
    private readonly IAppLog _log;
    private readonly object _gate = new();
    private readonly object _saveGate = new(); // serializes _store.Save calls across the timer, Flush and Dispose.
    private readonly Debouncer _debouncer;

    private AppSettings _current;
    private long _version;       // bumped on every Update that lands.
    private long _savedVersion;  // version last successfully written to the store.
    private bool _disposed;

    public SettingsCoordinator(ISettingsStore store, IAppLog log, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(log);

        _store = store;
        _log = log;
        _current = AppSettingsNormalizer.Normalize(store.Load());
        _debouncer = new Debouncer(SaveDelay, SaveNow, timeProvider);
    }

    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event EventHandler<AppSettings>? Changed;

    /// Normalizes the result of <paramref name="change"/>, updates <see cref="Current"/>, raises
    /// <see cref="Changed"/> synchronously on the calling thread, then schedules a debounced save.
    /// A version counter (rather than a dirty flag) records the update: a save that started before this
    /// call returns can never clear it, so the new value is always eventually persisted — by the pending
    /// debounce, by a later <see cref="Flush"/>, or by <see cref="Dispose"/>.
    public void Update(Func<AppSettings, AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        AppSettings updated;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            updated = AppSettingsNormalizer.Normalize(change(_current));
            _current = updated;
            _version++;
        }

        try
        {
            Changed?.Invoke(this, updated);
        }
        finally
        {
            // Always schedule the save, even if a Changed subscriber threw.
            lock (_gate)
            {
                if (!_disposed)
                {
                    _debouncer.Signal();
                }
            }
        }
    }

    /// Synchronous save if the current value hasn't been persisted yet (app exit).
    public void Flush() => SaveNow();

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        SaveNow();
        _debouncer.Dispose();
    }

    private void SaveNow()
    {
        // Serializes concurrent callers (the debounce timer, Flush from the UI thread, Dispose) so that at most
        // one _store.Save call is ever in flight, and so a snapshot taken here always reflects the latest Current
        // as of the moment this caller got its turn (never a stale value from a save that started earlier).
        lock (_saveGate)
        {
            AppSettings toSave;
            long version;
            lock (_gate)
            {
                version = _version;
                if (version == _savedVersion)
                {
                    return; // Nothing changed since the last successful save.
                }

                toSave = _current;
            }

            try
            {
                _store.Save(toSave);
                lock (_gate)
                {
                    _savedVersion = version;
                }
            }
            catch (Exception ex)
            {
                _log.Write(AppLogLevel.Error, "Settings", $"Failed to save settings to '{_store.FilePath}'.", ex);
            }
        }
    }
}
