using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MdReader.App.Documents;
using MdReader.Edge;
using MdReader.Shell.Documents;
using MdReader.Shell.ViewModels;
using MdReader.Core.Diagnostics;
using MdReader.Core.Documents;
using MdReader.Core.Protocol;
using MdReader.Core.Settings;
using MdReader.Core.Theming;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MdReader.App.Views;

/// <summary>
/// View of one document tab (ARCHITECTURE §4.10–§4.12). Owns the tab's WebView2 and its <see cref="WebViewBridge"/>, the
/// find bar, zoom and theme plumbing, and the automation surface (<c>DocumentView</c>: Name = full path, ItemStatus =
/// loading / rendered:&lt;version&gt; / error:&lt;kind&gt;).
/// </summary>
/// <remarks>
/// The WebView2 control is created once and never re-parented. It is replaced by a new control (and bridge) only to
/// recover, from the WPF error's "Try again", after a failed initialization or after the browser process exited — both
/// leave the old control unusable.
/// </remarks>
public partial class DocumentView : UserControl, IDocumentTabView
{
    private const string Category = "DocumentView";
    private const double ZoomEpsilon = 0.001;
    private const string RestartRequiredMessage = "Restart MdReader to continue.";

    private readonly DocumentTabViewModel? _tab;
    private bool _splitView;
    private bool _suppressEditorChange;
    private WebView2? _webView;
    private WebViewBridge? _bridge;
    private Window? _window;
    private bool _initStarted;
    private bool _initializing;
    private bool _webViewInitialized;
    private HostRecovery _recovery;
    private bool _browserProcessLost;   // set by a browser-process exit; cleared by a successful re-creation
    private bool _disposed;

    /// What "Try again" on the WPF error does.
    private enum HostRecovery
    {
        None,               // nothing can recover inside this process: Retry disabled, "Restart MdReader to continue."
        Renavigate,         // the CoreWebView2 is alive: reload the page
        RecreateWebView,    // the control is unusable: build a new WebView2 + bridge with the shared environment
    }

    /// Designer / XAML only: a view without a tab shows nothing.
    public DocumentView()
    {
        InitializeComponent();
    }

    internal DocumentView(DocumentTabViewModel tab)
        : this()
    {
        _tab = tab;
        DocumentViewServices services = tab.ViewServices;
        AutomationProperties.SetName(this, tab.FilePath);
        UpdateItemStatus();

        CreateWebView();
        FindBarControl.Attach(() => _bridge, FocusWebView, services.Status, services.Log);

        tab.Session.StateChanged += OnSessionStateChanged;
        tab.Session.EditorTextReplaced += OnEditorTextReplaced;
        services.Theme.EffectiveThemeChanged += OnEffectiveThemeChanged;
        services.Settings.Changed += OnSettingsChanged;
        Loaded += OnLoaded;
        IsVisibleChanged += OnIsVisibleChanged;

        // handledEventsToo: F3/Esc must reach the find bar even if the window-level shortcut router marked them handled.
        AddHandler(PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDownInView), handledEventsToo: true);

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

    // ----------------------------------------------------------------------------------------------------------------
    // Split view (§4.10)

    internal bool IsSplitView => _splitView;

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
            EditorBox.Visibility = Visibility.Visible;
            EditorSplitter.Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (!_disposed && _splitView && IsVisible)
                {
                    EditorBox.Focus();
                }
            });
        }
        else
        {
            EditorBox.Visibility = Visibility.Collapsed;
            EditorSplitter.Visibility = Visibility.Collapsed;
            EditorColumn.Width = new GridLength(0);
            SplitterColumn.Width = new GridLength(0);
            PreviewColumn.Width = new GridLength(1, GridUnitType.Star);
            _tab.Session.EndEditing();   // keeps the buffer while it is dirty, so reopening the pane restores it
            FocusWebViewDeferred();
        }
    }

    private void ApplySplitRatio(double ratio)
    {
        double clamped = Math.Clamp(ratio, AppSettings.MinSplitRatio, AppSettings.MaxSplitRatio);
        EditorColumn.Width = new GridLength(clamped, GridUnitType.Star);
        SplitterColumn.Width = GridLength.Auto;
        PreviewColumn.Width = new GridLength(1 - clamped, GridUnitType.Star);
    }

    private void SetEditorText(string text)
    {
        _suppressEditorChange = true;
        try
        {
            EditorBox.Text = text;
        }
        finally
        {
            _suppressEditorChange = false;
        }
    }

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_disposed || _tab is null || _suppressEditorChange || !_splitView)
        {
            return;
        }

        _tab.Session.SetEditorText(EditorBox.Text);
    }

    /// The session replaced the buffer (reload, save, a ticked checkbox): adopt it without losing the caret.
    private void OnEditorTextReplaced(object? sender, string text)
    {
        if (_disposed || !_splitView || string.Equals(EditorBox.Text, text, StringComparison.Ordinal))
        {
            return;
        }

        int caret = EditorBox.CaretIndex;
        int firstVisibleLine = EditorBox.GetFirstVisibleLineIndex();
        SetEditorText(text);
        EditorBox.CaretIndex = Math.Min(caret, text.Length);
        if (firstVisibleLine > 0)
        {
            EditorBox.ScrollToLine(Math.Min(firstVisibleLine, Math.Max(0, EditorBox.LineCount - 1)));
        }
    }

    private void OnSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        if (_disposed || _tab is null || !_splitView)
        {
            return;
        }

        double editor = EditorColumn.ActualWidth;
        double preview = PreviewColumn.ActualWidth;
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
        Loaded -= OnLoaded;
        IsVisibleChanged -= OnIsVisibleChanged;
        RemoveHandler(PreviewKeyDownEvent, new KeyEventHandler(OnPreviewKeyDownInView));
        if (_window is not null)
        {
            _window.Activated -= OnWindowActivated;
            _window = null;
        }

        DestroyWebView(detachSession: false);   // the session is already disposed
    }

    // ----------------------------------------------------------------------------------------------------------------
    // WebView lifetime

    private void CreateWebView()
    {
        DocumentViewServices services = _tab!.ViewServices;
        var webView = new WebView2();
        AutomationProperties.SetAutomationId(webView, "WebView");

        var bridge = new WebViewBridge(webView, services.Theme, services.Settings, services.Paths, services.Perf, services.Log, services.Time);

        // Before the controller exists, so the first frame already has the page background (no white flash).
        ApplyPageBackground(bridge, services.Theme.EffectiveTheme);
        webView.AllowExternalDrop = true;
        bridge.SetZoom(services.Settings.Current.Zoom);
        bridge.ZoomChanged += OnZoomFactorChanged;
        webView.GotFocus += OnWebViewGotFocus;
        bridge.HostFailed += OnBridgeHostFailed;

        _webView = webView;
        _bridge = bridge;
        _webViewInitialized = false;
        WebViewHost.Children.Add(webView);
    }

    private void DestroyWebView(bool detachSession)
    {
        FindBarControl.ResetSession();   // the find session dies with the control
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

        if (_webView is { } webView)
        {
            _webView = null;
            webView.GotFocus -= OnWebViewGotFocus;
            try
            {
                webView.Dispose();
            }
            catch (Exception ex)
            {
                _tab?.ViewServices.Log.Write(AppLogLevel.Warning, Category, "Disposing the WebView2 control failed.", ex);
            }

            WebViewHost.Children.Remove(webView);
        }

        _webViewInitialized = false;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_disposed || _tab is null)
        {
            return;
        }

        AttachToWindow();

        // Loaded can fire again (e.g. after the host is re-templated); the WebView is initialized exactly once here.
        if (_initStarted)
        {
            return;
        }

        _initStarted = true;
        await InitializeWebViewAsync();
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
        WebViewBridge bridge = _bridge;
        try
        {
            // The shared layer only knows IWebViewEnvironmentProvider; the WebView2 environment itself comes from the
            // backend's own contract, which the composition root registers for the same singleton.
            var provider = (IWebView2EnvironmentProvider)services.Environment;
            CoreWebView2Environment environment = await provider.GetAsync();
            string resourceRoot = await tab.Session.ResourceRootTask;
            if (_disposed || !ReferenceEquals(bridge, _bridge))
            {
                return;
            }

            await bridge.InitializeAsync(environment, resourceRoot);
            if (_disposed || !ReferenceEquals(bridge, _bridge))
            {
                return;
            }

            _webViewInitialized = true;
            _browserProcessLost = false;
            tab.Session.Attach(bridge);
            if (IsVisible)
            {
                FocusWebViewDeferred();
            }
        }
        catch (Exception ex) when (!_disposed)
        {
            services.Log.Write(AppLogLevel.Error, Category, $"Couldn't initialize the WebView for {tab.FilePath}.", ex);

            // A failed initialization leaves the control unusable: a faulted EnsureCoreWebView2Async can't be retried
            // on it, and a half-initialized bridge refuses a second InitializeAsync. Drop both; "Try again" builds new
            // ones. If this was already the attempt to recover from a dead browser process, give up inside this process.
            DestroyWebView(detachSession: true);
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
    // WPF error for a page that can't be shown

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
        if (_webView is not null)
        {
            _webView.Visibility = Visibility.Hidden;
        }

        HostErrorPanel.Visibility = Visibility.Visible;
        _tab.Session.ReportHostError(kind);
        if (recovery == HostRecovery.None)
        {
            _tab.ViewServices.Log.Write(AppLogLevel.Error, Category,
                $"The WebView can't be recreated after the browser process exited; MdReader has to be restarted ({path}).");
            _tab.ViewServices.Status.ShowStatus(RestartRequiredMessage);
        }
    }

    private async void OnHostErrorRetryClick(object sender, RoutedEventArgs e)
    {
        if (_disposed || _tab is null || _initializing || _recovery == HostRecovery.None)
        {
            return;
        }

        HostRecovery recovery = _recovery;
        HostErrorPanel.Visibility = Visibility.Collapsed;
        _tab.Session.ClearHostError();
        if (recovery == HostRecovery.Renavigate && _bridge is { CanRestart: true } bridge)
        {
            if (_webView is not null)
            {
                _webView.Visibility = Visibility.Visible;
            }

            bridge.Restart();
            return;
        }

        _tab.ViewServices.Log.Write(AppLogLevel.Info, Category, $"Recreating the WebView for {_tab.FilePath}.");
        DestroyWebView(detachSession: true);
        CreateWebView();
        await InitializeWebViewAsync();
    }

    // ----------------------------------------------------------------------------------------------------------------
    // State, theme, zoom

    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        UpdateItemStatus();
        UpdateEditorEditable();
    }

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

    private void UpdateItemStatus()
    {
        if (_tab is null)
        {
            return;
        }

        DocumentSession session = _tab.Session;
        string status = session.State switch
        {
            DocumentSessionState.Error => "error:" + DocumentSession.ToProtocolName(session.ErrorKind ?? DocumentErrorKind.RenderFailed),
            _ when session.RenderedVersion > 0 => "rendered:" + session.RenderedVersion.ToString(CultureInfo.InvariantCulture),
            _ => "loading",
        };

        if (!string.Equals(AutomationProperties.GetItemStatus(this), status, StringComparison.Ordinal))
        {
            AutomationProperties.SetItemStatus(this, status);
        }
    }

    private void OnEffectiveThemeChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnEffectiveThemeChanged(sender, e));
            return;
        }

        if (!_disposed && _tab is not null && _bridge is { } bridge)
        {
            ApplyPageBackground(bridge, _tab.ViewServices.Theme.EffectiveTheme);
            bridge.SetPreferredColorScheme(_tab.ViewServices.Theme.EffectiveTheme);
        }
    }

    /// The colour the WebView paints before the page has drawn anything (§10).
    private static void ApplyPageBackground(WebViewBridge bridge, AppTheme theme)
    {
        (byte r, byte g, byte b) = ThemePalette.PageBackground(theme);
        bridge.SetBackgroundColor(r, g, b);
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnSettingsChanged(sender, settings));
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
        if (_disposed || _tab is null || sender is not WebViewBridge bridge || !ReferenceEquals(bridge, _bridge))
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
        Window? window = Window.GetWindow(this);
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

    private void OnWebViewGotFocus(object sender, RoutedEventArgs e) => NotifyUserInteraction();

    /// R6: the first focus / activation / key input after a print dialog leaves print mode if the page didn't.
    private void NotifyUserInteraction()
    {
        if (_disposed || _tab is null || !_tab.Session.IsPrintFallbackArmed)
        {
            return;
        }

        // Deferred: this can run inside a synchronous WebView2 callback (focus, accelerator keys) where CoreWebView2
        // calls fail (§4.12).
        DocumentSession session = _tab.Session;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (!_disposed)
            {
                session.OnUserInteractionAfterPrint();
            }
        });
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (e.NewValue is true)
        {
            FocusWebViewDeferred();
        }
        else
        {
            FindBarControl.Close(focusWebView: false);   // switching tabs closes the find bar
        }
    }

    /// F3 / Shift+F3 and Esc for the find bar. Keys forwarded from the WebView arrive here while the browser is blocked
    /// (AcceleratorKeyPressed is synchronous), so the CoreWebView2 work is always deferred (§4.12).
    private void OnPreviewKeyDownInView(object sender, KeyEventArgs e)
    {
        if (_disposed || _tab is null)
        {
            return;
        }

        NotifyUserInteraction();
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        ModifierKeys modifiers = Keyboard.Modifiers;
        if (key == Key.F3 && (modifiers & ~ModifierKeys.Shift) == ModifierKeys.None)
        {
            e.Handled = true;
            bool backwards = (modifiers & ModifierKeys.Shift) != 0;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (!_disposed)
                {
                    FindBarControl.FindFromShortcut(backwards);
                }
            });
        }
        else if (key == Key.Escape && modifiers == ModifierKeys.None && !e.IsRepeat && FindBarControl.IsOpen)
        {
            e.Handled = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (!_disposed)
                {
                    FindBarControl.Close(focusWebView: true);
                }
            });
        }
    }

    private void FocusWebViewDeferred() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            // Only inside an active window: focusing the WebView of a background window could steal activation.
            if (_disposed || !IsVisible || FindBarControl.IsKeyboardFocusWithin || Window.GetWindow(this) is not { IsActive: true })
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
        });

    private void FocusWebView()
    {
        if (_disposed || !_webViewInitialized || _webView is not { Visibility: Visibility.Visible } webView)
        {
            return;
        }

        try
        {
            webView.Focus();
        }
        catch (Exception ex)
        {
            _tab?.ViewServices.Log.Write(AppLogLevel.Debug, Category, "Focusing the WebView failed.", ex);
        }
    }
}
