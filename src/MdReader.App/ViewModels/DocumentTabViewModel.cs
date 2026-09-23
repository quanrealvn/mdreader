using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using MdReader.App.Documents;
using MdReader.App.Services;
using MdReader.App.Views;
using MdReader.Core.Diagnostics;
using MdReader.Core.Hosting;
using MdReader.Core.Protocol;
using MdReader.Core.Settings;

namespace MdReader.App.ViewModels;

/// <summary>Services the tab's <see cref="DocumentView"/> needs; the view is created by DocumentHost, not by DI.</summary>
internal sealed record DocumentViewServices(
    IWebViewEnvironmentProvider Environment,
    SettingsCoordinator Settings,
    IThemeService Theme,
    AppPaths Paths,
    IPerfRecorder Perf,
    IStatusNotifier Status,
    IAppLog Log,
    TimeProvider Time);

/// <summary>
/// One document tab (ARCHITECTURE §4.10–§4.12). Created by <see cref="DocumentOpener"/> through ActivatorUtilities together
/// with its <see cref="DocumentSession"/>. UI thread only; every PropertyChanged is raised on the UI thread.
/// </summary>
internal sealed class DocumentTabViewModel : IDocumentTab
{
    private const string Category = "DocumentTab";

    private readonly DocumentSession _session;
    private readonly ITabHost _tabHost;
    private readonly IDialogService _dialogs;
    private readonly IStatusNotifier _status;
    private readonly IAppLog _log;
    private DocumentView? _view;
    private bool _isActive;
    private string _title;
    private bool _isDeleted;
    private bool _isSplitView;
    private bool _isDirty;
    private DocumentSessionState _state;
    private bool _disposed;

    public DocumentTabViewModel(
        DocumentSession session,
        ITabHost tabHost,
        IDialogService dialogs,
        IStatusNotifier status,
        IWebViewEnvironmentProvider environment,
        SettingsCoordinator settings,
        IThemeService theme,
        AppPaths paths,
        IPerfRecorder perf,
        IAppLog log,
        TimeProvider time)
    {
        _session = session;
        _tabHost = tabHost;
        _dialogs = dialogs;
        _status = status;
        _log = log;
        ViewServices = new DocumentViewServices(environment, settings, theme, paths, perf, status, log, time);

        string fileName = Path.GetFileName(session.Path);
        Header = string.IsNullOrEmpty(fileName) ? session.Path : fileName;
        _title = session.Title;
        _isDeleted = session.IsDeleted;
        _state = session.State;
        CloseCommand = new CloseTabCommand(this);
        _isSplitView = settings.Current.SplitView;
        _session.StateChanged += OnSessionStateChanged;
        _session.DirtyChanged += OnSessionDirtyChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int DocId => _session.DocId;

    public string FilePath => _session.Path;

    public string Header { get; }

    public string ToolTip => _session.Path;

    public string Title => _title;

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
            {
                return;
            }

            _isActive = value;
            OnPropertyChanged();
        }
    }

    public bool IsDeleted => _isDeleted;

    /// Preview only, or Markdown source + preview (§4.10). The setting remembers the last mode a tab was put into.
    /// Closing the pane while the buffer has unsaved changes is refused: the changes would still be there, and still be
    /// saved when the tab closes, with nothing on screen to show for them.
    public bool IsSplitView
    {
        get => _isSplitView;
        set
        {
            if (_isSplitView == value || _disposed)
            {
                return;
            }

            if (!value && _session.IsDirty)
            {
                _status.ShowStatus("Save (Ctrl+S) or reload (F5) before closing the editor: it has unsaved changes.");
                OnPropertyChanged();   // the toolbar toggle snapped off: put it back
                return;
            }

            _isSplitView = value;
            _view?.SetSplitView(value);
            OnPropertyChanged();
        }
    }

    public bool IsDirty => _isDirty;

    public DocumentSessionState State => _state;

    public ICommand CloseCommand { get; }

    internal DocumentSession Session => _session;

    internal DocumentViewServices ViewServices { get; }

    internal DocumentView? View => _view;

    public Task SaveAsync() => _disposed || !_session.IsDirty ? Task.CompletedTask : SaveCoreAsync();

    /// Closing a tab or the window. Asks before overwriting a file that changed on disk, exactly as Ctrl+S does; saying
    /// no returns false, which the caller reads as "don't close this tab".
    public bool SaveBlocking()
    {
        if (_disposed || !_session.IsDirty)
        {
            return false;
        }

        if (_session.WouldOverwriteDiskChanges && !_dialogs.ConfirmOverwriteChangedFile(Header))
        {
            _log.Write(AppLogLevel.Info, Category, $"Not saving {FilePath} on close: the user declined to overwrite the newer file.");
            return false;
        }

        return _session.SaveBlocking();
    }

    /// The Windows session is ending (§4.11): save the text, no questions — there is no one left to answer them, and
    /// the alternative is throwing the text away.
    public bool SaveForSessionEnd()
    {
        if (_disposed || !_session.IsDirty)
        {
            return false;
        }

        if (_session.WouldOverwriteDiskChanges)
        {
            _log.Write(AppLogLevel.Warning, Category,
                $"{FilePath} changed on disk while it had unsaved changes; the Windows session is ending, so the editor's version wins.");
        }

        return _session.SaveBlocking();
    }

    private async Task SaveCoreAsync()
    {
        if (_session.WouldOverwriteDiskChanges && !_dialogs.ConfirmOverwriteChangedFile(Header))
        {
            return;
        }

        await _session.SaveAsync();
    }

    public Task ReloadAsync() => _disposed ? Task.CompletedTask : _session.ReloadAsync(preserveScroll: true);

    public void ScrollTo(string fragment)
    {
        if (!_disposed)
        {
            _session.ScrollTo(fragment);
        }
    }

    public void ToggleToc()
    {
        if (!_disposed)
        {
            _session.ToggleToc();
        }
    }

    public void ShowFind()
    {
        if (!_disposed)
        {
            _view?.ShowFind();
        }
    }

    public Task PrintAsync() => _disposed ? Task.CompletedTask : _session.PrintAsync();

    public async Task ExportPdfAsync()
    {
        if (_disposed)
        {
            return;
        }

        string suggested = Path.ChangeExtension(FilePath, ".pdf");
        string? target = _dialogs.ShowSavePdfDialog(suggested);
        if (string.IsNullOrWhiteSpace(target) || _disposed)
        {
            return;
        }

        await _session.ExportPdfAsync(target);
    }

    public Task WhenRenderedAsync(RenderPhase phase, TimeSpan timeout) => _session.WhenRenderedAsync(phase, timeout);

    public Task CapturePreviewAsync(Stream pngDestination) => _session.CapturePreviewAsync(pngDestination);

    /// Called once by the tab's DocumentView (created by DocumentHost).
    internal void AttachView(DocumentView view)
    {
        if (_view is not null && !ReferenceEquals(_view, view))
        {
            throw new InvalidOperationException("The tab already has a view.");
        }

        _view = view;
        if (_isSplitView)
        {
            view.SetSplitView(true);
        }
    }

    /// §4.11 order: find session → DocumentSession (watcher, CTS) → WebView2.
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DocumentView? view = _view;
        try
        {
            view?.StopFind();
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Warning, Category, "Stopping the find session failed.", ex);
        }

        _session.StateChanged -= OnSessionStateChanged;
        _session.DirtyChanged -= OnSessionDirtyChanged;
        try
        {
            _session.Dispose();
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Warning, Category, "Disposing the document session failed.", ex);
        }

        try
        {
            view?.DisposeWebView();
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Warning, Category, "Disposing the WebView failed.", ex);
        }
    }

    private void OnSessionDirtyChanged(object? sender, EventArgs e)
    {
        if (_isDirty == _session.IsDirty)
        {
            return;
        }

        _isDirty = _session.IsDirty;
        OnPropertyChanged(nameof(IsDirty));
    }

    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        if (_title != _session.Title)
        {
            _title = _session.Title;
            OnPropertyChanged(nameof(Title));
        }

        if (_isDeleted != _session.IsDeleted)
        {
            _isDeleted = _session.IsDeleted;
            OnPropertyChanged(nameof(IsDeleted));
        }

        if (_state != _session.State)
        {
            _state = _session.State;
            OnPropertyChanged(nameof(State));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class CloseTabCommand(DocumentTabViewModel owner) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
            if (!owner._disposed)
            {
                owner._tabHost.Close(owner);
            }
        }
    }
}
