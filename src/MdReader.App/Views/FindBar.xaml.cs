using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MdReader.App.Services;
using MdReader.Core.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace MdReader.App.Views;

/// <summary>
/// WPF find bar over the native WebView2 Find API (ARCHITECTURE §4.12): 150 ms debounced restart on text change,
/// "3/18" match count, Enter/Shift+Enter (F3/Shift+F3 and Esc are routed here by <see cref="DocumentView"/>).
/// If the runtime lacks the Find API it disables itself with a status message (risk R15).
/// </summary>
public partial class FindBar : UserControl
{
    private const string Category = "FindBar";
    private const string UnavailableMessage = "Find isn't available with the installed WebView2 Runtime. Update it to use Find.";
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(150);

    // HRESULTs meaning "the installed runtime doesn't implement the Find API".
    private const int EPointerNotImplemented = unchecked((int)0x80004001);   // E_NOTIMPL
    private const int ENoInterface = unchecked((int)0x80004002);             // E_NOINTERFACE
    private const int RegDbClassNotRegistered = unchecked((int)0x80040154);  // REGDB_E_CLASSNOTREG

    private readonly DispatcherTimer _debounce;
    private Func<CoreWebView2?>? _coreProvider;
    private Action? _focusWebView;
    private IStatusNotifier? _status;
    private IAppLog _log = NullAppLog.Instance;
    private CoreWebView2Find? _find;
    private string _activeTerm = "";
    private int _searchGeneration;
    private bool _unavailable;

    public FindBar()
    {
        InitializeComponent();
        _debounce = new DispatcherTimer(DispatcherPriority.Input) { Interval = DebounceDelay };
        _debounce.Tick += OnDebounceTick;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    internal void Attach(Func<CoreWebView2?> coreProvider, Action focusWebView, IStatusNotifier status, IAppLog log)
    {
        _coreProvider = coreProvider;
        _focusWebView = focusWebView;
        _status = status;
        _log = log;
    }

    /// Ctrl+F: show, focus the box and select its text. An existing term is searched again right away.
    public void Open()
    {
        if (_unavailable)
        {
            _status?.ShowStatus(UnavailableMessage);
            return;
        }

        Visibility = Visibility.Visible;
        FocusTextBox();
        // A bar that was collapsed until now may not be focusable before its first layout pass: make sure after it.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (IsOpen && !FindTextBox.IsKeyboardFocusWithin)
            {
                FocusTextBox();
            }
        });
        if (FindTextBox.Text.Length > 0 && !string.Equals(_activeTerm, FindTextBox.Text, StringComparison.Ordinal))
        {
            _debounce.Stop();
            _ = RestartAsync();
        }
    }

    /// Esc / close button / tab switch: stop the search (clears highlights) and hide.
    public void Close(bool focusWebView)
    {
        _debounce.Stop();
        StopFind();
        Visibility = Visibility.Collapsed;
        if (focusWebView)
        {
            _focusWebView?.Invoke();
        }
    }

    /// F3 / Shift+F3: next/previous match, or open the bar when there is nothing to search for.
    public void FindFromShortcut(bool backwards)
    {
        if (!IsOpen || FindTextBox.Text.Length == 0)
        {
            Open();
            return;
        }

        Navigate(backwards);
    }

    /// Ends the native find session (tab close, bar close). Safe to call at any time.
    public void StopFind()
    {
        _searchGeneration++;
        _activeTerm = "";
        try
        {
            _find?.Stop();
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Debug, Category, "Find.Stop failed.", ex);
        }

        UpdateCount();
    }

    private void FocusTextBox()
    {
        FindTextBox.Focus();
        Keyboard.Focus(FindTextBox);
        FindTextBox.SelectAll();
    }

    private void Navigate(bool backwards)
    {
        if (_unavailable)
        {
            return;
        }

        // A term that hasn't been searched yet starts a new search from the first match.
        if (_debounce.IsEnabled || !string.Equals(_activeTerm, FindTextBox.Text, StringComparison.Ordinal) || _find is null)
        {
            _debounce.Stop();
            _ = RestartAsync();
            return;
        }

        try
        {
            if (backwards)
            {
                _find.FindPrevious();
            }
            else
            {
                _find.FindNext();
            }
        }
        catch (Exception ex) when (IsUnsupported(ex))
        {
            Disable(ex);
        }
        catch (Exception ex)
        {
            MarkTransientFailure("Moving to the next match", ex);
        }
    }

    private async Task RestartAsync()
    {
        int generation = ++_searchGeneration;
        string term = FindTextBox.Text;
        CoreWebView2? core = _coreProvider?.Invoke();
        if (core is null || _unavailable)
        {
            _activeTerm = "";
            UpdateCount();
            return;
        }

        try
        {
            CoreWebView2Find find = EnsureFind(core);
            find.Stop();
            _activeTerm = term;
            if (term.Length == 0)
            {
                UpdateCount();
                return;
            }

            CoreWebView2FindOptions options = core.Environment.CreateFindOptions();
            options.FindTerm = term;
            options.IsCaseSensitive = false;
            options.ShouldMatchWord = false;
            options.SuppressDefaultFindDialog = true;
            options.ShouldHighlightAllMatches = true;
            await find.StartAsync(options);
            if (generation == _searchGeneration)
            {
                UpdateCount();
            }
        }
        catch (Exception ex) when (IsUnsupported(ex))
        {
            Disable(ex);
        }
        catch (Exception ex)
        {
            if (generation == _searchGeneration)
            {
                MarkTransientFailure("Starting a find session", ex);
            }
            else
            {
                _log.Write(AppLogLevel.Debug, Category, "A superseded find session failed.", ex);
            }
        }
    }

    private CoreWebView2Find EnsureFind(CoreWebView2 core)
    {
        if (_find is null)
        {
            CoreWebView2Find find = core.Find;
            find.MatchCountChanged += OnFindCountersChanged;
            find.ActiveMatchIndexChanged += OnFindCountersChanged;
            _find = find;
        }

        return _find;
    }

    private void OnFindCountersChanged(object? sender, object e) => UpdateCount();

    private void UpdateCount()
    {
        try
        {
            if (_activeTerm.Length == 0 || _find is null)
            {
                FindMatchCount.Text = "";
                return;
            }

            int count = Math.Max(0, _find.MatchCount);
            int active = Math.Max(0, _find.ActiveMatchIndex);   // 1-based, −1 = none
            FindMatchCount.Text = string.Create(CultureInfo.InvariantCulture, $"{active}/{count}");
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Debug, Category, "Reading the match count failed.", ex);
        }
    }

    private void Disable(Exception ex)
    {
        _log.Write(AppLogLevel.Warning, Category, "The WebView2 Find API isn't available; find is disabled for this tab.", ex);
        _unavailable = true;
        _debounce.Stop();
        FindTextBox.IsEnabled = false;
        FindPreviousButton.IsEnabled = false;
        FindNextButton.IsEnabled = false;
        Close(focusWebView: true);
        _status?.ShowStatus(UnavailableMessage);
    }

    /// Only "this runtime has no Find API" disables find for good (R15). Anything else (a renderer that crashed or was
    /// busy, a transient COM error) is retried on the next input.
    private static bool IsUnsupported(Exception ex) =>
        ex is NotImplementedException
        || (ex is COMException or InvalidCastException && ex.HResult is EPointerNotImplemented or ENoInterface or RegDbClassNotRegistered);

    private void MarkTransientFailure(string what, Exception ex)
    {
        _log.Write(AppLogLevel.Warning, Category, $"{what} failed; find will retry on the next input.", ex);
        _activeTerm = "";            // the next Enter / F3 / text change starts a fresh search
        FindMatchCount.Text = "—";
    }

    /// The tab's WebView2 is being replaced (recovery): the old CoreWebView2Find dies with it.
    internal void ResetSession()
    {
        _debounce.Stop();
        StopFind();
        if (_find is { } find)
        {
            try
            {
                find.MatchCountChanged -= OnFindCountersChanged;
                find.ActiveMatchIndexChanged -= OnFindCountersChanged;
            }
            catch (Exception ex)
            {
                _log.Write(AppLogLevel.Debug, Category, "Unsubscribing from the old find session failed.", ex);
            }

            _find = null;
        }

        UpdateCount();
    }

    private void OnFindTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsOpen)
        {
            return;
        }

        _debounce.Stop();
        _debounce.Start();
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounce.Stop();
        _ = RestartAsync();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && FindTextBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            bool backwards = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Navigate(backwards));
        }
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e) => Navigate(backwards: true);

    private void OnNextClick(object sender, RoutedEventArgs e) => Navigate(backwards: false);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close(focusWebView: true);
}
