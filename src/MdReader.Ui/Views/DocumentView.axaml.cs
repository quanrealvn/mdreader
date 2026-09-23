using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MdReader.Core.Diagnostics;
using MdReader.Core.Documents;
using MdReader.Core.Protocol;
using MdReader.Core.Settings;
using MdReader.Core.Theming;
using MdReader.Shell.Documents;
using MdReader.Shell.ViewModels;
using MdReader.Ui.Commands;
using MdReader.Ui.Documents;

namespace MdReader.Ui.Views;

/// <summary>
/// View of one document tab (ARCHITECTURE §4.10–§4.12). Owns the tab's web view and its
/// <see cref="IHostedWebViewChannel"/>, the find bar, the editor pane, zoom and theme plumbing, and the keyboard hook for the
/// keys pressed inside the native web view.
/// </summary>
/// <remarks>
/// The native host window is created once and never re-parented. Only the controller is replaced (with a new channel),
/// from the error panel's "Try again", after a failed initialization or after the browser process exited — both leave
/// the old controller unusable.
/// </remarks>
public partial class DocumentView : UserControl, IDocumentTabView, IFindableView
{
    private const string Category = "DocumentView";
    private const double ZoomEpsilon = 0.001;
    private const string RestartRequiredMessage = "Restart MdReader to continue.";

    private readonly DocumentTabViewModel? _tab;
    private readonly AvaloniaShortcutRouter? _shortcuts;
    private readonly IDocumentWebViewHost? _host;
    private bool _splitView;
    private bool _suppressEditorChange;
    private IHostedWebViewChannel? _bridge;
    private Window? _window;
    private bool _initStarted;
    private bool _initializing;
    private bool _webViewInitialized;
    private HostRecovery _recovery;
    private bool _browserProcessLost;   // set by a browser-process exit; cleared by a successful re-creation
    private bool _disposed;

    /// What "Try again" on the error panel does.
    private enum HostRecovery
    {
        None,               // nothing can recover inside this process: Retry disabled, "Restart MdReader to continue."
        Renavigate,         // the web view is alive: reload the page
        RecreateWebView,    // the controller is unusable: build a new one with the shared environment
    }

    /// Designer / XAML only: a view without a tab shows nothing.
    public DocumentView()
    {
        InitializeComponent();
    }

    internal DocumentView(DocumentTabViewModel tab, AvaloniaShortcutRouter shortcuts)
        : this()
    {
        _tab = tab;
        _shortcuts = shortcuts;
        DocumentViewServices services = tab.ViewServices;
        AutomationProperties.SetName(this, tab.FilePath);

        _host = DocumentWebViewHostFactory.Create(services);
        _host.AcceleratorKeyPressed += OnAcceleratorKeyPressed;
        _host.WebViewFocused += OnWebViewFocused;
        WebViewHost.Children.Add(_host.Control);

        CreateChannel();
        _bridge!.SetZoom(services.Settings.Current.Zoom);   // global setting; applied when the web view is created
        FindBarControl.Attach(() => _bridge, FocusWebView, services.Status, services.Log);
        EditorBox.TextChanged += OnEditorTextChanged;

        tab.Session.EditorTextReplaced += OnEditorTextReplaced;
        tab.Session.StateChanged += OnSessionStateChanged;
        services.Theme.EffectiveThemeChanged += OnEffectiveThemeChanged;
        services.Settings.Changed += OnSettingsChanged;

        // handledEventsToo: F3/Esc must reach the find bar even if the window-level shortcut router marked them handled.
        AddHandler(KeyDownEvent, OnPreviewKeyDownInView, RoutingStrategies.Tunnel, handledEventsToo: true);

        tab.AttachView(this);
    }

    /// Ctrl+F (deferred by the keyboard router).
    public void ShowFind()
    {
        if (!_disposed && _tab is not null)
        {
            FindBarControl.Open();
        }
    }

    public void StopFind()
    {
        if (_tab is not null)
        {
            FindBarControl.Close(focusWebView: false);
        }
    }

    /// <summary>The macOS Edit menu's ⌘G / ⇧⌘G, which is the same thing F3 / Shift+F3 does on Windows.</summary>
    void IFindableView.FindFromMenu(bool backwards)
    {
        if (!_disposed && _tab is not null)
        {
            FindBarControl.FindFromShortcut(backwards);
        }
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Split view (§4.10)

    /// Shows or hides the editor pane. Opening it seeds the text box from the session's buffer and focuses it.
    public void SetSplitView(bool enabled)
    {
        if (_disposed || _tab is null || _splitView == enabled)
        {
            return;
        }

        _splitView = enabled;
        if (enabled)
        {
            UpdateEditorEditable();
            string text = _tab.Session.BeginEditing();
            if (!string.Equals(EditorBox.Text, text, StringComparison.Ordinal))
            {
                SetEditorText(text);
            }

            ApplySplitRatio(_tab.ViewServices.Settings.Current.SplitRatio);
            EditorBox.IsVisible = true;
            EditorSplitter.IsVisible = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed && _splitView && IsEffectivelyVisible)
                {
                    EditorBox.Focus();
                }
            }, DispatcherPriority.Input);
        }
        else
        {
            EditorBox.IsVisible = false;
            EditorSplitter.IsVisible = false;
            SplitGrid.ColumnDefinitions[0].Width = new GridLength(0);
            SplitGrid.ColumnDefinitions[1].Width = new GridLength(0);
            SplitGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            _tab.Session.EndEditing();   // keeps the buffer while it is dirty, so reopening the pane restores it
            FocusWebViewDeferred();
        }
    }

    private void ApplySplitRatio(double ratio)
    {
        double clamped = Math.Clamp(ratio, AppSettings.MinSplitRatio, AppSettings.MaxSplitRatio);
        SplitGrid.ColumnDefinitions[0].Width = new GridLength(clamped, GridUnitType.Star);
        SplitGrid.ColumnDefinitions[1].Width = GridLength.Auto;
        SplitGrid.ColumnDefinitions[2].Width = new GridLength(1 - clamped, GridUnitType.Star);
    }

    private void SetEditorText(string text)
    {
        _suppressEditorChange = true;
        Vector offset = EditorScroller?.Offset ?? default;
        int caret = EditorBox.CaretIndex;
        try
        {
            EditorBox.Text = text;
            EditorBox.CaretIndex = Math.Min(caret, text.Length);
            if (EditorScroller is { } scroller)
            {
                scroller.Offset = offset;   // Avalonia has no ScrollToLine; the pixel offset keeps the view still
            }
        }
        finally
        {
            _suppressEditorChange = false;
        }
    }

    private ScrollViewer? EditorScroller => EditorBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private void OnEditorTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_disposed || _tab is null || _suppressEditorChange || !_splitView)
        {
            return;
        }

        _tab.Session.SetEditorText(EditorBox.Text ?? "");
    }

    /// The session replaced the buffer (reload, save, a ticked checkbox): adopt it without losing the caret.
    private void OnEditorTextReplaced(object? sender, string text)
    {
        if (_disposed || !_splitView || string.Equals(EditorBox.Text, text, StringComparison.Ordinal))
        {
            return;
        }

        SetEditorText(text);
    }

    private void OnSplitterDragCompleted(object? sender, VectorEventArgs e)
    {
        if (_disposed || _tab is null || !_splitView)
        {
            return;
        }

        double editor = SplitGrid.ColumnDefinitions[0].ActualWidth;
        double preview = SplitGrid.ColumnDefinitions[2].ActualWidth;
        double total = editor + preview;
        if (total <= 0)
        {
            return;
        }

        double ratio = Math.Clamp(editor / total, AppSettings.MinSplitRatio, AppSettings.MaxSplitRatio);
        ApplySplitRatio(ratio);
        _tab.ViewServices.Settings.Update(s => Math.Abs(s.SplitRatio - ratio) < 0.005 ? s : s with { SplitRatio = ratio });
    }

    /// Last step of the tab's Dispose (§4.11): after the find session and the DocumentSession.
    public void DisposeWebView()
    {
        if (_disposed || _tab is null)
        {
            return;
        }

        _disposed = true;
        DocumentViewServices services = _tab.ViewServices;
        _tab.Session.StateChanged -= OnSessionStateChanged;
        _tab.Session.EditorTextReplaced -= OnEditorTextReplaced;
        services.Theme.EffectiveThemeChanged -= OnEffectiveThemeChanged;
        services.Settings.Changed -= OnSettingsChanged;
        EditorBox.TextChanged -= OnEditorTextChanged;
        RemoveHandler(KeyDownEvent, OnPreviewKeyDownInView);
        if (_window is not null)
        {
            _window.Activated -= OnWindowActivated;
            _window = null;
        }

        DestroyChannel(detachSession: false);   // the session is already disposed
        if (_host is not null)
        {
            _host.AcceleratorKeyPressed -= OnAcceleratorKeyPressed;
            _host.WebViewFocused -= OnWebViewFocused;
            _host.Shutdown();
            _host.Dispose();
        }
    }

    // ----------------------------------------------------------------------------------------------------------------
    // WebView lifetime

    private void CreateChannel()
    {
        DocumentViewServices services = _tab!.ViewServices;
        IHostedWebViewChannel bridge = _host!.CreateChannel();

        // Before the controller exists, so the first frame already has the page background (no white flash).
        ApplyPageBackground(bridge, services.Theme.EffectiveTheme);
        bridge.ZoomChanged += OnZoomFactorChanged;
        bridge.HostFailed += OnBridgeHostFailed;

        _bridge = bridge;
        _webViewInitialized = false;
    }

    private void DestroyChannel(bool detachSession)
    {
        FindBarControl.ResetSession();   // the find session dies with the controller
        if (_bridge is { } bridge)
        {
            _bridge = null;
            bridge.HostFailed -= OnBridgeHostFailed;
            bridge.ZoomChanged -= OnZoomFactorChanged;
            if (detachSession)
            {
                _tab?.Session.Detach(bridge);
            }

            bridge.Dispose();
        }

        _host?.DestroyWebView();
        _webViewInitialized = false;
    }

    /// <summary>
    /// Starts the tab's web view as soon as the view joins the window's visual tree. It deliberately does not wait for
    /// <c>Loaded</c>: Avalonia raises that from the layout pass, and a background tab is never laid out, so its document
    /// would only start loading the first time the user switched to it. The native host window is created on attach
    /// either way, which is what the controller needs.
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_disposed || _tab is null)
        {
            return;
        }

        AttachToWindow();
        if (_initStarted)
        {
            return;
        }

        _initStarted = true;
        _ = InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        if (_initializing || _disposed || _tab is null || _bridge is null)
        {
            return;
        }

        _initializing = true;
        DocumentTabViewModel tab = _tab;
        DocumentViewServices services = tab.ViewServices;
        IHostedWebViewChannel bridge = _bridge;
        try
        {
            string resourceRoot = await tab.Session.ResourceRootTask;
            if (_disposed || !ReferenceEquals(bridge, _bridge))
            {
                return;
            }

            await _host!.InitializeAsync(bridge, resourceRoot);
            if (_disposed || !ReferenceEquals(bridge, _bridge))
            {
                return;
            }

            _webViewInitialized = true;
            _browserProcessLost = false;
            tab.Session.Attach(bridge);
            if (IsEffectivelyVisible)
            {
                FocusWebViewDeferred();
            }
        }
        catch (Exception ex) when (!_disposed)
        {
            services.Log.Write(AppLogLevel.Error, Category, $"Couldn't initialize the WebView for {tab.FilePath}.", ex);

            // A failed initialization leaves the controller unusable: it can't be created twice, and a half-initialized
            // channel refuses a second InitializeAsync. Drop both; "Try again" builds new ones. If this was already the
            // attempt to recover from a dead browser process, give up inside this process.
            DestroyChannel(detachSession: true);
            ShowHostError(DocumentErrorKind.RenderFailed, ex.Message,
                _browserProcessLost ? HostRecovery.None : HostRecovery.RecreateWebView);
        }
        catch (Exception ex)
        {
            services.Log.Write(AppLogLevel.Debug, Category, "WebView initialization ended because the tab was closed.", ex);
        }
        finally
        {
            _initializing = false;
        }
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Shell error for a page that can't be shown

    private void OnBridgeHostFailed(object? sender, WebViewHostFailedEventArgs e)
    {
        if (!ReferenceEquals(sender, _bridge))
        {
            return;
        }

        if (e.RequiresNewWebView)
        {
            _browserProcessLost = true;
            ShowHostError(e.Kind, e.Detail, HostRecovery.RecreateWebView);
        }
        else
        {
            ShowHostError(e.Kind, e.Detail, HostRecovery.Renavigate);
        }
    }

    private void ShowHostError(DocumentErrorKind kind, string detail, HostRecovery recovery)
    {
        if (_disposed || _tab is null)
        {
            return;
        }

        string path = _tab.FilePath;
        string title;
        string message;
        try
        {
            (title, message) = DocumentErrorMessages.For(kind, path, detail);
        }
        catch (Exception ex)
        {
            _tab.ViewServices.Log.Write(AppLogLevel.Error, Category, "DocumentErrorMessages.For failed.", ex);
            (title, message) = ("Couldn't display this document", detail);
        }

        HostErrorTitle.Text = title;
        HostErrorMessage.Text = string.IsNullOrWhiteSpace(detail) || message.Contains(detail, StringComparison.Ordinal)
            ? message
            : $"{message}\n{detail}";
        HostErrorPath.Text = path;
        _recovery = recovery;
        HostErrorRetryButton.IsEnabled = recovery != HostRecovery.None;
        FindBarControl.Close(focusWebView: false);

        // The native child window is always on top of Avalonia's own drawing, so the error panel only shows once the
        // web view is hidden (airspace).
        if (_host is not null)
        {
            _host.Control.IsVisible = false;
        }

        HostErrorPanel.IsVisible = true;
        _tab.Session.ReportHostError(kind);
        if (recovery == HostRecovery.None)
        {
            _tab.ViewServices.Log.Write(AppLogLevel.Error, Category,
                $"The WebView can't be recreated after the browser process exited; MdReader has to be restarted ({path}).");
            _tab.ViewServices.Status.ShowStatus(RestartRequiredMessage);
        }
    }

    private async void OnHostErrorRetryClick(object? sender, RoutedEventArgs e)
    {
        if (_disposed || _tab is null || _initializing || _recovery == HostRecovery.None)
        {
            return;
        }

        HostRecovery recovery = _recovery;
        HostErrorPanel.IsVisible = false;
        _tab.Session.ClearHostError();
        if (_host is not null)
        {
            _host.Control.IsVisible = true;
        }

        if (recovery == HostRecovery.Renavigate && _bridge is { CanRestart: true } bridge)
        {
            bridge.Restart();
            return;
        }

        _tab.ViewServices.Log.Write(AppLogLevel.Info, Category, $"Recreating the WebView for {_tab.FilePath}.");
        DestroyChannel(detachSession: true);
        CreateChannel();
        await InitializeWebViewAsync();
    }

    // ----------------------------------------------------------------------------------------------------------------
    // State, theme, zoom

    private void OnSessionStateChanged(object? sender, EventArgs e) => UpdateEditorEditable();

    /// <summary>
    /// The editor only takes input once the document has actually been read (§4.10). Before that — a first load still
    /// running, or one that failed — the box shows nothing and is read-only, so no keystroke can end up overwriting a
    /// file MdReader never managed to read.
    /// </summary>
    private void UpdateEditorEditable()
    {
        if (_tab is null)
        {
            return;
        }

        bool readOnly = !_tab.Session.CanEdit;
        if (EditorBox.IsReadOnly != readOnly)
        {
            EditorBox.IsReadOnly = readOnly;
        }
    }

    private void OnEffectiveThemeChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnEffectiveThemeChanged(sender, e));
            return;
        }

        if (!_disposed && _tab is not null && _bridge is { } bridge)
        {
            ApplyPageBackground(bridge, _tab.ViewServices.Theme.EffectiveTheme);
            bridge.SetPreferredColorScheme(_tab.ViewServices.Theme.EffectiveTheme);
        }
    }

    /// The colour the web view paints before the page has drawn anything (§10).
    private static void ApplyPageBackground(IWebViewChannel bridge, AppTheme theme)
    {
        (byte r, byte g, byte b) = ThemePalette.PageBackground(theme);
        bridge.SetBackgroundColor(r, g, b);
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnSettingsChanged(sender, settings));
            return;
        }

        if (_disposed || _bridge is not { } bridge)
        {
            return;
        }

        try
        {
            // Zoom is global: every tab follows the setting; our own echo (|Δ| < 0.001) is ignored.
            if (Math.Abs(bridge.Zoom - settings.Zoom) >= ZoomEpsilon)
            {
                bridge.SetZoom(settings.Zoom);
            }
        }
        catch (Exception ex)
        {
            _tab?.ViewServices.Log.Write(AppLogLevel.Warning, Category, "Applying the zoom setting failed.", ex);
        }
    }

    /// Ctrl+wheel inside the page (IsZoomControlEnabled) → persist the global zoom.
    private void OnZoomFactorChanged(object? sender, EventArgs e)
    {
        if (_disposed || _tab is null || sender is not IHostedWebViewChannel bridge || !ReferenceEquals(bridge, _bridge))
        {
            return;
        }

        try
        {
            SettingsCoordinator settings = _tab.ViewServices.Settings;
            double zoom = bridge.Zoom;
            if (Math.Abs(zoom - settings.Current.Zoom) < ZoomEpsilon)
            {
                return;
            }

            double clamped = ZoomLevels.Clamp(zoom);
            settings.Update(s => s with { Zoom = clamped });
            if (Math.Abs(bridge.Zoom - clamped) >= ZoomEpsilon)
            {
                bridge.SetZoom(clamped);
            }
        }
        catch (Exception ex)
        {
            _tab.ViewServices.Log.Write(AppLogLevel.Warning, Category, "Persisting the zoom level failed.", ex);
        }
    }

    // ----------------------------------------------------------------------------------------------------------------
    // Focus, keys, print-mode fallback

    private void AttachToWindow()
    {
        var window = TopLevel.GetTopLevel(this) as Window;
        if (ReferenceEquals(window, _window))
        {
            return;
        }

        if (_window is not null)
        {
            _window.Activated -= OnWindowActivated;
        }

        _window = window;
        if (window is not null)
        {
            window.Activated += OnWindowActivated;
        }
    }

    private void OnWindowActivated(object? sender, EventArgs e) => NotifyUserInteraction();

    private void OnWebViewFocused(object? sender, EventArgs e) => NotifyUserInteraction();

    /// R6: the first focus / activation / key input after a print dialog leaves print mode if the page didn't.
    private void NotifyUserInteraction()
    {
        if (_disposed || _tab is null || !_tab.Session.IsPrintFallbackArmed)
        {
            return;
        }

        // Deferred: this can run inside a synchronous web-view callback (focus, forwarded keys) where calls back
        // into the web view fail (§4.12).
        DocumentSession session = _tab.Session;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                session.OnUserInteractionAfterPrint();
            }
        }, DispatcherPriority.Input);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_disposed || change.Property != IsVisibleProperty)
        {
            return;
        }

        if (change.GetNewValue<bool>())
        {
            FocusWebViewDeferred();
        }
        else
        {
            FindBarControl.Close(focusWebView: false);   // switching tabs closes the find bar
        }
    }

    /// <summary>
    /// The keys pressed while focus is inside the page. WebView2 raises this synchronously with the browser process
    /// blocked, so nothing here may call back into the web view: the window shortcuts defer through the shared
    /// router, and the find-bar keys are posted (§4.12). Nothing arrives this way on macOS, where the menu bar
    /// dispatches shortcuts before the page is offered the key.
    /// </summary>
    private void OnAcceleratorKeyPressed(object? sender, ForwardedKeyEventArgs e)
    {
        if (_disposed || _tab is null)
        {
            return;
        }

        NotifyUserInteraction();
        if (_shortcuts?.HandleForwardedKey(e) == true)
        {
            e.Handled = true;
            return;
        }

        if (e.IsKeyUp)
        {
            return;
        }

        // F3 / Shift+F3 and Esc: the rows the shared map leaves to the document view.
        switch (e.Key)
        {
            case MdReader.Shell.Commands.ShortcutKey.F3:
                e.Handled = true;
                HandleFindNextKey((AvaloniaShortcutRouter.CurrentModifiers() & MdReader.Shell.Commands.ShortcutModifiers.Shift) != 0);
                break;
            case MdReader.Shell.Commands.ShortcutKey.Escape when FindBarControl.IsOpen:
                e.Handled = true;
                HandleCloseFindKey();
                break;
            default:
                break;
        }
    }

    /// F3 / Shift+F3 and Esc pressed while focus is in the Avalonia chrome (find box, editor, toolbar).
    private void OnPreviewKeyDownInView(object? sender, KeyEventArgs e)
    {
        if (_disposed || _tab is null)
        {
            return;
        }

        NotifyUserInteraction();
        ShortcutModifiersFromEvent(e, out bool shift, out bool onlyShift);
        if (e.Key == Key.F3 && onlyShift)
        {
            e.Handled = true;
            HandleFindNextKey(shift);
        }
        else if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && FindBarControl.IsOpen)
        {
            e.Handled = true;
            HandleCloseFindKey();
        }
    }

    private static void ShortcutModifiersFromEvent(KeyEventArgs e, out bool shift, out bool shiftOnly)
    {
        shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
        shiftOnly = (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None;
    }

    private void HandleFindNextKey(bool backwards) => Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed)
        {
            FindBarControl.FindFromShortcut(backwards);
        }
    }, DispatcherPriority.Input);

    private void HandleCloseFindKey() => Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed)
        {
            FindBarControl.Close(focusWebView: true);
        }
    }, DispatcherPriority.Input);

    private void FocusWebViewDeferred() =>
        Dispatcher.UIThread.Post(() =>
        {
            // Only inside an active window: focusing the WebView of a background window could steal activation.
            if (_disposed || !IsEffectivelyVisible || FindBarControl.IsKeyboardFocusWithin
                || TopLevel.GetTopLevel(this) is not Window { IsActive: true })
            {
                return;
            }

            if (_splitView)
            {
                EditorBox.Focus();   // in split view the editor is where the user is working
                return;
            }

            if (_webViewInitialized)
            {
                FocusWebView();
            }
        }, DispatcherPriority.Input);

    private void FocusWebView()
    {
        if (_disposed || !_webViewInitialized || _host is not { Control.IsVisible: true } host)
        {
            return;
        }

        host.FocusWebView();
    }
}
