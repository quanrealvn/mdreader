using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using System.Windows.Threading;
using MdReader.App.Services;
using MdReader.Core.Diagnostics;
using MdReader.Core.Paths;
using MdReader.Core.Settings;
using MdReader.Core.Theming;

namespace MdReader.App.ViewModels;

/// Main window state: the tab list (ITabHost), toolbar commands, recent files, zoom/TOC/theme display and the status toast
/// (IStatusNotifier). UI thread only, except ShowStatus and settings notifications which marshal themselves.
public sealed class MainViewModel : ObservableObject, ITabHost, IStatusNotifier, IDisposable
{
    internal const string AppName = "MdReader";
    internal static readonly TimeSpan StatusDuration = TimeSpan.FromSeconds(4);

    /// Long enough for the page's tocVisibilityChanged round trip in docked mode, so the button doesn't flicker.
    private static readonly TimeSpan TocButtonResyncDelay = TimeSpan.FromMilliseconds(300);
    private const string Category = "Main";

    private readonly ObservableCollection<IDocumentTab> _tabs = [];
    private readonly SettingsCoordinator _settings;
    private readonly IThemeService _theme;
    private readonly IDialogService _dialogs;
    private readonly Lazy<IDocumentOpener> _opener;
    private readonly IAppLog _log;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _tocButtonResync;
    private readonly RelayCommand[] _documentCommands;

    private IDocumentTab? _activeTab;
    private AppSettings _settingsSnapshot;
    private IReadOnlyList<RecentFileEntry> _recentFiles = [];
    private string _statusText = "";
    private bool _isStatusVisible;
    private bool _disposed;

    // DocumentOpener depends on ITabHost (this class), so the opener is resolved lazily to break the cycle.
    public MainViewModel(SettingsCoordinator settings, IThemeService theme, IDialogService dialogs, Lazy<IDocumentOpener> opener,
                         IAppLog log)
    {
        _settings = settings;
        _theme = theme;
        _dialogs = dialogs;
        _opener = opener;
        _log = log;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Tabs = new ReadOnlyObservableCollection<IDocumentTab>(_tabs);
        _tabs.CollectionChanged += OnTabsChanged;

        _statusTimer = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = StatusDuration };
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            IsStatusVisible = false;
        };

        OpenCommand = new RelayCommand(Open);
        OpenRecentCommand = new RelayCommand(p => OpenRecent(p as string), p => p is string { Length: > 0 });
        ClearRecentFilesCommand = new RelayCommand(ClearRecentFiles, () => _recentFiles.Count > 0);
        CloseActiveTabCommand = new RelayCommand(() => { if (_activeTab is { } tab) Close(tab); }, () => HasTabs);
        NextTabCommand = new RelayCommand(() => ActivateRelative(+1), () => _tabs.Count > 1);
        PreviousTabCommand = new RelayCommand(() => ActivateRelative(-1), () => _tabs.Count > 1);
        FindCommand = new RelayCommand(() => _activeTab?.ShowFind(), () => HasTabs);
        PrintCommand = new RelayCommand(() => RunOnActiveTab(t => t.PrintAsync(), "Print"), () => HasTabs);
        ExportPdfCommand = new RelayCommand(() => RunOnActiveTab(t => t.ExportPdfAsync(), "Export as PDF"), () => HasTabs);
        ReloadCommand = new RelayCommand(() => RunOnActiveTab(t => t.ReloadAsync(), "Reload"), () => HasTabs);
        ToggleTocCommand = new RelayCommand(ToggleToc, () => HasTabs);
        _tocButtonResync = new DispatcherTimer(DispatcherPriority.Normal, _dispatcher) { Interval = TocButtonResyncDelay };
        _tocButtonResync.Tick += (_, _) =>
        {
            _tocButtonResync.Stop();
            OnPropertyChanged(nameof(IsTocVisible));   // re-read: the button shows the persisted preference again
        };
        CycleThemeCommand = new RelayCommand(CycleTheme);
        ZoomInCommand = new RelayCommand(() => UpdateZoom(ZoomLevels.StepUp));
        ZoomOutCommand = new RelayCommand(() => UpdateZoom(ZoomLevels.StepDown));
        ZoomResetCommand = new RelayCommand(() => UpdateZoom(_ => ZoomLevels.Default));
        _documentCommands =
        [
            (RelayCommand)CloseActiveTabCommand, (RelayCommand)NextTabCommand, (RelayCommand)PreviousTabCommand,
            (RelayCommand)FindCommand, (RelayCommand)PrintCommand, (RelayCommand)ExportPdfCommand, (RelayCommand)ReloadCommand,
            (RelayCommand)ToggleTocCommand,
        ];

        _settingsSnapshot = settings.Current;
        _recentFiles = ToEntries(_settingsSnapshot.RecentFiles);
        _settings.Changed += OnSettingsChanged;
        _theme.EffectiveThemeChanged += OnEffectiveThemeChanged;
    }

    // ----- ITabHost -----

    public ReadOnlyObservableCollection<IDocumentTab> Tabs { get; }

    public IDocumentTab? ActiveTab => _activeTab;

    public void Add(IDocumentTab tab, bool activate)
    {
        ArgumentNullException.ThrowIfNull(tab);
        _dispatcher.VerifyAccess();
        if (_tabs.Contains(tab))
        {
            if (activate)
            {
                Activate(tab);
            }

            return;
        }

        tab.PropertyChanged += OnTabPropertyChanged;
        _tabs.Add(tab);
        if (activate || _tabs.Count == 1)
        {
            Activate(tab);
        }
    }

    public void Activate(IDocumentTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        _dispatcher.VerifyAccess();
        if (ReferenceEquals(tab, _activeTab) || !_tabs.Contains(tab))
        {
            return;
        }

        var previous = _activeTab;
        _activeTab = tab;
        if (previous is not null)
        {
            previous.IsActive = false;
        }

        tab.IsActive = true;
        RaiseActiveTabChanged();
    }

    public void Close(IDocumentTab tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        _dispatcher.VerifyAccess();
        var index = _tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        var wasActive = ReferenceEquals(tab, _activeTab);
        if (wasActive)
        {
            _activeTab = null;   // before removal, so a selector reacting to the removal never sees the closed tab as active
        }

        tab.PropertyChanged -= OnTabPropertyChanged;
        _tabs.RemoveAt(index);
        if (wasActive)
        {
            tab.IsActive = false;
            if (_tabs.Count > 0)
            {
                Activate(_tabs[Math.Min(index, _tabs.Count - 1)]);
            }
            else
            {
                RaiseActiveTabChanged();
            }
        }

        DisposeTab(tab);
    }

    /// Shutdown (§4.11): closes every tab — each Dispose tears down find → session → WebView — while the window and the
    /// browser are still alive, without activating neighbors on the way. Idempotent. UI thread.
    public void CloseAllTabs()
    {
        _dispatcher.VerifyAccess();
        if (_tabs.Count == 0)
        {
            return;
        }

        var active = _activeTab;
        _activeTab = null;
        if (active is not null)
        {
            active.IsActive = false;
        }

        while (_tabs.Count > 0)
        {
            var tab = _tabs[^1];
            tab.PropertyChanged -= OnTabPropertyChanged;
            _tabs.RemoveAt(_tabs.Count - 1);
            DisposeTab(tab);
        }

        RaiseActiveTabChanged();
    }

    // ----- IStatusNotifier -----

    public void ShowStatus(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => ShowStatus(message));
            return;
        }

        _log.Write(AppLogLevel.Info, Category, $"Status: {message}");
        StatusText = message;
        IsStatusVisible = true;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    // ----- Bindable state -----

    /// Two-way bound to the tab strip's selection.
    public IDocumentTab? SelectedTab
    {
        get => _activeTab;
        set
        {
            if (value is not null)
            {
                Activate(value);
            }
            else
            {
                OnPropertyChanged();   // the selector cleared its selection transiently; push the active tab back
            }
        }
    }

    public bool HasTabs => _tabs.Count > 0;

    public string WindowTitle => _activeTab is null ? AppName : $"{DisplayTitle(_activeTab)} - {AppName}";

    public string ZoomText => FormatZoom(_settingsSnapshot.Zoom);

    /// The persisted *docked* TOC preference; TocToggleButton.IsChecked mirrors it. Setting it (a click or a UIA Toggle
    /// on the button — the TwoWay binding's only writer) means "toggle the TOC" rather than "store this value": below
    /// 900 CSS px the page toggles its drawer and the preference stays as it is (final-review S1). Every set is one press,
    /// even a quick second one while the button still shows the pressed state. The button snaps back to the preference
    /// shortly afterwards; in docked mode the page's tocVisibilityChanged has updated it by then.
    public bool IsTocVisible
    {
        get => _settingsSnapshot.TocVisible;
        set
        {
            ToggleToc();
            _tocButtonResync.Stop();
            _tocButtonResync.Start();
        }
    }

    /// Ctrl+B and the TOC button: the active page decides (drawer vs docked sidebar). Without a tab there is no page,
    /// so the preference itself is flipped.
    private void ToggleToc()
    {
        if (_activeTab is { } tab)
        {
            tab.ToggleToc();
        }
        else
        {
            _settings.Update(s => s with { TocVisible = !s.TocVisible });
        }
    }

    /// RecentFileEntry items (FullPath, FileName, Folder), most recent first. Typed as object: the entry type is internal.
    public IReadOnlyList<object> RecentFiles => _recentFiles;

    public bool HasRecentFiles => _recentFiles.Count > 0;

    /// More ▾ → "Colorful style" (ReadingStyleMenuItem, checkable, TwoWay): checked = Colorful, unchecked = Classic (§14).
    /// Every open tab follows through SettingsCoordinator.Changed (DocumentSession posts readingStyle).
    public bool IsColorfulStyle
    {
        get => _settingsSnapshot.ReadingStyle == ReadingStyle.Colorful;
        set
        {
            var style = value ? ReadingStyle.Colorful : ReadingStyle.Classic;
            _settings.Update(s => s.ReadingStyle == style ? s : s with { ReadingStyle = style });
        }
    }

    /// Segoe Fluent Icons glyph for the current theme preference (System = half sun, Light = sun, Dark = moon).
    public string ThemeGlyph => _theme.Preference switch
    {
        ThemePreference.Light => "\uE706",
        ThemePreference.Dark => "\uE708",
        _ => "\uE793",
    };

    /// ThemeButton HelpText (§4.12): "Theme: Dark (system)", "Theme: Light", "Theme: Dark".
    public string ThemeDescription => _theme.Preference switch
    {
        ThemePreference.Light => "Theme: Light",
        ThemePreference.Dark => "Theme: Dark",
        _ => $"Theme: {_theme.EffectiveTheme} (system)",
    };

    public string ThemeToolTip => $"{ThemeDescription} · Click for {NextPreference(_theme.Preference)}";

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsStatusVisible
    {
        get => _isStatusVisible;
        private set => SetProperty(ref _isStatusVisible, value);
    }

    // ----- Commands -----

    public ICommand OpenCommand { get; }

    public ICommand OpenRecentCommand { get; }

    public ICommand ClearRecentFilesCommand { get; }

    public ICommand CloseActiveTabCommand { get; }

    public ICommand NextTabCommand { get; }

    public ICommand PreviousTabCommand { get; }

    public ICommand FindCommand { get; }

    public ICommand PrintCommand { get; }

    public ICommand ExportPdfCommand { get; }

    public ICommand ReloadCommand { get; }

    public ICommand ToggleTocCommand { get; }

    public ICommand CycleThemeCommand { get; }

    public ICommand ZoomInCommand { get; }

    public ICommand ZoomOutCommand { get; }

    public ICommand ZoomResetCommand { get; }

    /// Opens files dropped onto the WPF chrome: Markdown files only (by extension); the first one is activated.
    public void OpenDroppedFiles(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var markdown = paths.Where(p => !string.IsNullOrWhiteSpace(p) && MarkdownFileTypes.IsMarkdownPath(p)).ToList();
        if (markdown.Count < paths.Count)
        {
            ShowStatus(markdown.Count == 0
                ? "Only Markdown files open in MdReader."
                : "Some files were skipped: only Markdown files open in MdReader.");
        }

        OpenFiles(markdown);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        _theme.EffectiveThemeChanged -= OnEffectiveThemeChanged;
        _statusTimer.Stop();
        _tocButtonResync.Stop();

        // Exit path: the window is already closed. The collection is deliberately left as is: clearing it would make the
        // DocumentHost mutate a visual tree whose application resources are being torn down (WPF throws there).
        var tabs = _tabs.ToArray();
        _activeTab = null;
        foreach (var tab in tabs)
        {
            tab.PropertyChanged -= OnTabPropertyChanged;
            DisposeTab(tab);
        }
    }

    // ----- Implementation -----

    private void Open()
    {
        var initialDirectory = _activeTab is not null
            ? Path.GetDirectoryName(_activeTab.FilePath)
            : _recentFiles.Count > 0 ? _recentFiles[0].Folder : null;
        OpenFiles(_dialogs.ShowOpenDialog(initialDirectory));
    }

    private void OpenFiles(IReadOnlyList<string> paths)
    {
        for (var i = 0; i < paths.Count; i++)
        {
            _opener.Value.Open(paths[i], fragment: null, activate: i == 0);
        }
    }

    /// §9 "recent entry removed if opened from Recent" when the file is gone: DocumentOpener removes the entry when the
    /// first load of a tab reports NotFound, and the tab shows the "File not found" view.
    private void OpenRecent(string? path)
    {
        if (!string.IsNullOrEmpty(path))
        {
            _opener.Value.Open(path);
        }
    }

    private void ClearRecentFiles() => _settings.Update(s => s with { RecentFiles = [] });

    private void ActivateRelative(int delta)
    {
        if (_tabs.Count < 2)
        {
            return;
        }

        var index = _activeTab is null ? 0 : _tabs.IndexOf(_activeTab);
        var next = ((index + delta) % _tabs.Count + _tabs.Count) % _tabs.Count;
        Activate(_tabs[next]);
    }

    private void CycleTheme()
    {
        _theme.SetPreference(NextPreference(_theme.Preference));
        RaiseThemeChanged();
    }

    private void UpdateZoom(Func<double, double> change) =>
        _settings.Update(s => s with { Zoom = ZoomLevels.Clamp(change(s.Zoom)) });

    private async void RunOnActiveTab(Func<IDocumentTab, Task> action, string operation)
    {
        var tab = _activeTab;
        if (tab is null)
        {
            return;
        }

        try
        {
            await action(tab);
        }
        catch (OperationCanceledException)
        {
            // canceled by a newer request or by closing the tab
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, Category, $"{operation} failed for {tab.FilePath}", ex);
            ShowStatus($"{operation} failed: {ex.Message}");
        }
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasTabs));
        foreach (var command in _documentCommands)
        {
            command.NotifyCanExecuteChanged();
        }
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _activeTab))
        {
            return;
        }

        if (e.PropertyName is null or nameof(IDocumentTab.Title) or nameof(IDocumentTab.Header))
        {
            if (_dispatcher.CheckAccess())
            {
                OnPropertyChanged(nameof(WindowTitle));
            }
            else
            {
                _dispatcher.BeginInvoke(() => OnPropertyChanged(nameof(WindowTitle)));
            }
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (_dispatcher.CheckAccess())
        {
            ApplySettings(settings);
        }
        else
        {
            _dispatcher.BeginInvoke(() => ApplySettings(_settings.Current));
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        if (_disposed)
        {
            return;
        }

        var previous = _settingsSnapshot;
        _settingsSnapshot = settings;
        if (Math.Abs(previous.Zoom - settings.Zoom) > 0.0001)
        {
            OnPropertyChanged(nameof(ZoomText));
        }

        if (previous.TocVisible != settings.TocVisible)
        {
            OnPropertyChanged(nameof(IsTocVisible));
        }

        if (previous.ReadingStyle != settings.ReadingStyle)
        {
            OnPropertyChanged(nameof(IsColorfulStyle));
        }

        if (!previous.RecentFiles.SequenceEqual(settings.RecentFiles, StringComparer.Ordinal))
        {
            _recentFiles = ToEntries(settings.RecentFiles);
            OnPropertyChanged(nameof(RecentFiles));
            OnPropertyChanged(nameof(HasRecentFiles));
            ((RelayCommand)ClearRecentFilesCommand).NotifyCanExecuteChanged();
        }

        if (previous.Theme != settings.Theme)
        {
            RaiseThemeChanged();
        }
    }

    private void OnEffectiveThemeChanged(object? sender, EventArgs e) => RaiseThemeChanged();

    private void RaiseThemeChanged()
    {
        OnPropertyChanged(nameof(ThemeGlyph));
        OnPropertyChanged(nameof(ThemeDescription));
        OnPropertyChanged(nameof(ThemeToolTip));
    }

    private void RaiseActiveTabChanged()
    {
        OnPropertyChanged(nameof(ActiveTab));
        OnPropertyChanged(nameof(SelectedTab));
        OnPropertyChanged(nameof(WindowTitle));
    }

    private void DisposeTab(IDocumentTab tab)
    {
        try
        {
            tab.Dispose();
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, Category, $"Disposing the tab for {tab.FilePath} failed", ex);
        }
    }

    private static string DisplayTitle(IDocumentTab tab) => string.IsNullOrWhiteSpace(tab.Title) ? tab.Header : tab.Title;

    internal static string FormatZoom(double zoom) => $"{Math.Round(zoom * 100, MidpointRounding.AwayFromZero):0}%";

    private static ThemePreference NextPreference(ThemePreference preference) => preference switch
    {
        ThemePreference.System => ThemePreference.Light,
        ThemePreference.Light => ThemePreference.Dark,
        _ => ThemePreference.System,
    };

    private static IReadOnlyList<RecentFileEntry> ToEntries(IReadOnlyList<string> paths) =>
        paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(RecentFileEntry.Create).ToList();
}

/// One recent file for display (menu item / empty-state row). Purely lexical: never touches the file system.
internal sealed record RecentFileEntry(string FullPath, string FileName, string Folder)
{
    internal static RecentFileEntry Create(string fullPath)
    {
        var fileName = Path.GetFileName(fullPath);
        return new RecentFileEntry(fullPath, fileName.Length > 0 ? fileName : fullPath, Path.GetDirectoryName(fullPath) ?? "");
    }
}
