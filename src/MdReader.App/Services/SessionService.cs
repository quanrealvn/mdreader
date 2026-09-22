using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using MdReader.App.ViewModels;
using MdReader.Core.Diagnostics;
using MdReader.Core.Paths;
using MdReader.Core.Settings;

namespace MdReader.App.Services;

/// <summary>
/// Reopens the last session's tabs at startup and keeps <see cref="AppSettings.Session"/> in step with the open tabs, their
/// order and the active tab (saved through <see cref="SettingsCoordinator"/>'s debounced save). Program uses it only in the
/// primary instance and never with --capture. UI thread, except the file existence checks.
/// </summary>
public sealed class SessionService
{
    private const string Category = "Session";

    private readonly MainViewModel _tabs;
    private readonly IDocumentOpener _opener;
    private readonly SettingsCoordinator _settings;
    private readonly IFileSystemProbe _probe;
    private readonly IAppLog _log;
    private readonly Dispatcher _dispatcher;

    private SessionState? _saved;
    private Task<IReadOnlyList<string>>? _existing;   // the saved files that still exist
    private bool _tracking;
    private bool _frozen;

    public SessionService(MainViewModel tabs, IDocumentOpener opener, SettingsCoordinator settings, IFileSystemProbe probe, IAppLog log)
    {
        _tabs = tabs;
        _opener = opener;
        _settings = settings;
        _probe = probe;
        _log = log;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// Starts checking which of the saved session's files still exist, on background threads (a network path can hang; paths
    /// that haven't answered within <see cref="SessionFileCheck.DefaultTimeout"/> are skipped). Call as early as possible.
    public void BeginRestore()
    {
        _saved = _settings.Current.Session;
        if (_saved is { Files.Count: > 0 })
        {
            _existing = SessionFileCheck.FilterExistingAsync(_saved.Files, _probe, SessionFileCheck.DefaultTimeout);
        }
    }

    /// Opens the startup tabs: the saved session's files in order (not activated), then the command-line files (the first
    /// one activated); without command-line files the saved active tab is activated. Never waits for the existence checks:
    /// if they're still running, the command-line files open now and the restored tabs are put in front of them later.
    /// Recording starts once the session is restored.
    public void OpenStartupFiles(IReadOnlyList<string> commandLineFiles)
    {
        ArgumentNullException.ThrowIfNull(commandLineFiles);
        _dispatcher.VerifyAccess();
        var existing = _existing;
        if (existing is { IsCompletedSuccessfully: true })
        {
            Restore(existing.Result, activateSaved: commandLineFiles.Count == 0);
            existing = null;
        }

        for (var i = 0; i < commandLineFiles.Count; i++)
        {
            _opener.Open(commandLineFiles[i], fragment: null, activate: i == 0);
        }

        if (existing is null)
        {
            StartRecording();
        }
        else
        {
            _ = RestoreWhenCheckedAsync(existing, activateSaved: commandLineFiles.Count == 0);
        }
    }

    /// Shutdown (§4.11), before the tabs are closed: records the session one last time and stops following the tabs, so
    /// closing them doesn't empty it. If the restore hasn't happened yet, the saved session is left as it was.
    public void Freeze()
    {
        _dispatcher.VerifyAccess();
        if (_frozen)
        {
            return;
        }

        if (_tracking)
        {
            Record();
            ((INotifyCollectionChanged)_tabs.Tabs).CollectionChanged -= OnTabsChanged;
            _tabs.PropertyChanged -= OnMainPropertyChanged;
            _tracking = false;
        }

        _frozen = true;
    }

    private async Task RestoreWhenCheckedAsync(Task<IReadOnlyList<string>> existing, bool activateSaved)
    {
        IReadOnlyList<string> files;
        try
        {
            files = await existing.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, Category, "Checking the saved session's files failed", ex);
            files = [];
        }

        await _dispatcher.InvokeAsync(() =>
        {
            if (_frozen)
            {
                return;   // closing already: the saved session stays as it was
            }

            Restore(files, activateSaved);
            StartRecording();
        });
    }

    private void Restore(IReadOnlyList<string> files, bool activateSaved)
    {
        var saved = _saved!;
        if (files.Count < saved.Files.Count)
        {
            _log.Write(AppLogLevel.Info, Category, $"Skipping {saved.Files.Count - files.Count} saved file(s) that are missing or didn't answer");
        }

        _log.Write(AppLogLevel.Info, Category, $"Restoring {files.Count} tab(s)");
        var activate = activateSaved && _tabs.ActiveTab is null;   // never take over a tab the user opened meanwhile
        var position = 0;
        foreach (var file in files)
        {
            _opener.Open(file, fragment: null, activate: false);

            // Tabs opened before the restore (command-line files, when the checks were slow) go after the restored ones.
            if (FindTab(file) is { } tab)
            {
                _tabs.MoveTab(tab, position++);
            }
        }

        if (activate && saved.ActiveFile is { } active && FindTab(active) is { } activeTab)
        {
            _tabs.Activate(activeTab);
        }
    }

    /// The tab DocumentOpener opened (or reused) for <paramref name="path"/>: same normalization, OrdinalIgnoreCase.
    private IDocumentTab? FindTab(string path)
    {
        string fullPath;
        try
        {
            // Like DocumentOpener: GetFullPath on a path with '~' may touch the network, so such a path is used as is.
            fullPath = Path.IsPathFullyQualified(path) && path.Contains('~', StringComparison.Ordinal) ? path : Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return _tabs.Tabs.FirstOrDefault(t => string.Equals(t.FilePath, fullPath, StringComparison.OrdinalIgnoreCase));
    }

    private void StartRecording()
    {
        if (_tracking || _frozen)
        {
            return;
        }

        _tracking = true;
        ((INotifyCollectionChanged)_tabs.Tabs).CollectionChanged += OnTabsChanged;
        _tabs.PropertyChanged += OnMainPropertyChanged;
        Record();
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Record();

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is null or nameof(MainViewModel.ActiveTab))
        {
            Record();
        }
    }

    /// Only tabs for real files count (fully-qualified paths); the settings normalizer caps the list.
    private void Record()
    {
        var files = _tabs.Tabs.Select(t => t.FilePath).Where(p => !string.IsNullOrEmpty(p) && Path.IsPathFullyQualified(p)).ToList();
        var active = _tabs.ActiveTab?.FilePath;
        var current = _settings.Current.Session;
        if (current is not null && current.Files.SequenceEqual(files, StringComparer.Ordinal)
            && string.Equals(current.ActiveFile, active, StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            _settings.Update(s => s with { Session = new SessionState(files, active) });
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Warning, Category, "Couldn't record the open tabs", ex);
        }
    }
}
