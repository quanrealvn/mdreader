using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using MdReader.Core.Diagnostics;
using MdReader.Shell.Documents;
using MdReader.Shell.Services;

namespace MdReader.App.Views;

/// <summary>
/// WPF find bar over <see cref="IWebViewChannel"/>'s find capability (ARCHITECTURE §4.12): 150 ms debounced restart on
/// text change, "3/18" match count, Enter/Shift+Enter (F3/Shift+F3 and Esc are routed here by <see cref="DocumentView"/>).
/// If the runtime has no find API the channel says so and the bar disables itself with a status message (risk R15).
/// </summary>
public partial class FindBar : UserControl
{
    private const string Category = "FindBar";
    private const string UnavailableMessage = "Find isn't available with the installed WebView2 Runtime. Update it to use Find.";
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(150);

    private readonly DispatcherTimer _debounce;
    private Func<IWebViewChannel?>? _channelProvider;
    private Action? _focusWebView;
    private IStatusNotifier? _status;
    private IAppLog _log = NullAppLog.Instance;
    private FindSession? _session;
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

    internal void Attach(Func<IWebViewChannel?> channelProvider, Action focusWebView, IStatusNotifier status, IAppLog log)
    {
        _channelProvider = channelProvider;
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

    /// Ends the find session (tab close, bar close). Safe to call at any time.
    public void StopFind()
    {
        _searchGeneration++;
        _activeTerm = "";
        try
        {
            _channelProvider?.Invoke()?.StopFind();
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Debug, Category, "StopFind failed.", ex);
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
        if (_debounce.IsEnabled || !string.Equals(_activeTerm, FindTextBox.Text, StringComparison.Ordinal) || _session is null)
        {
            _debounce.Stop();
            _ = RestartAsync();
            return;
        }

        IWebViewChannel? channel = _channelProvider?.Invoke();
        if (channel is null)
        {
            return;
        }

        try
        {
            if (backwards)
            {
                channel.FindPrevious();
            }
            else
            {
                channel.FindNext();
            }
        }
        catch (FindNotSupportedException ex)
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
        IWebViewChannel? channel = _channelProvider?.Invoke();
        if (channel is null || _unavailable)
        {
            _activeTerm = "";
            UpdateCount();
            return;
        }

        try
        {
            // The channel stops the running session synchronously, so _activeTerm may be adopted before the await.
            Task<FindSession?> starting = channel.StartFindAsync(term);
            _activeTerm = term;
            FindSession? session = await starting;
            if (session is null)
            {
                _activeTerm = "";
                UpdateCount();
                return;
            }

            AttachSession(session);
            if (term.Length == 0 || generation == _searchGeneration)
            {
                UpdateCount();
            }
        }
        catch (FindNotSupportedException ex)
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

    private void AttachSession(FindSession session)
    {
        if (ReferenceEquals(session, _session))
        {
            return;
        }

        if (_session is { } previous)
        {
            previous.CountersChanged -= OnFindCountersChanged;
        }

        _session = session;
        session.CountersChanged += OnFindCountersChanged;
    }

    private void OnFindCountersChanged(object? sender, EventArgs e) => UpdateCount();

    private void UpdateCount()
    {
        if (_activeTerm.Length == 0 || _session is not { } session)
        {
            FindMatchCount.Text = "";
            return;
        }

        int count = Math.Max(0, session.MatchCount);
        int active = Math.Max(0, session.ActiveMatchIndex);   // 1-based, −1 = none
        FindMatchCount.Text = string.Create(CultureInfo.InvariantCulture, $"{active}/{count}");
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

    private void MarkTransientFailure(string what, Exception ex)
    {
        _log.Write(AppLogLevel.Warning, Category, $"{what} failed; find will retry on the next input.", ex);
        _activeTerm = "";            // the next Enter / F3 / text change starts a fresh search
        FindMatchCount.Text = "—";
    }

    /// The tab's web view is being replaced (recovery): the find session dies with it.
    internal void ResetSession()
    {
        _debounce.Stop();
        StopFind();
        if (_session is { } session)
        {
            session.CountersChanged -= OnFindCountersChanged;
            _session = null;
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
