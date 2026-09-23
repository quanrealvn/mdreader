using MdReader.Core.Protocol;
using MdReader.Core.Theming;

namespace MdReader.Shell.Documents;

// Boundary between DocumentSession (logic) and the shell's web view. Everything the app asks of the browser goes
// through here, so the shared layer never names WebView2, WKWebView or WebKitGTK. UI thread only.
public interface IWebViewChannel
{
    bool IsReady { get; }                                     // 'ready' received for the current page load
    event EventHandler? Ready;                                // every (re)load, UI thread
    event EventHandler<WebMessage>? MessageReceived;          // validated messages except 'ready' and 'drop', UI thread
    event EventHandler<IReadOnlyList<string>>? FilesDropped;  // from 'drop' + AdditionalObjects, or file: navigation
    void Post(string json);                                   // UI thread; if !IsReady: logged and dropped

    /// True when local images can't be served because the resource root couldn't be mapped (path too long, etc.).
    bool LocalResourcesUnavailable { get; }

    /// Retries a resource-root mapping that failed because the document's folder didn't exist yet. Called before a
    /// render payload is posted, so a folder created later (file restored) still gets its images.
    void EnsureResourceRootMapped();

    // ----- Find (§4.12) -----

    /// Starts (or restarts) a find session for <paramref name="term"/>; null when there is no live page to search.
    /// Throws <see cref="FindNotSupportedException"/> when the platform has no find API at all (risk R15).
    Task<FindSession?> StartFindAsync(string term);

    void FindNext();

    void FindPrevious();

    /// Ends the current find session and clears its highlights. Safe to call at any time.
    void StopFind();

    // ----- Print, export, capture -----

    /// Opens the browser's own print dialog. Completes as soon as the dialog has been asked for, not when it closes.
    Task ShowPrintUiAsync();

    /// Prints the page to <paramref name="pdfPath"/> with backgrounds and without headers/footers.
    Task<bool> PrintToPdfAsync(string pdfPath);

    /// Writes a PNG of what the web view is showing. Throws when the page isn't ready.
    Task CapturePreviewAsync(Stream pngDestination);

    // ----- Zoom -----

    double Zoom { get; }

    void SetZoom(double zoomFactor);

    /// The user zoomed inside the page (Ctrl+wheel). UI thread.
    event EventHandler? ZoomChanged;

    // ----- Theme (§10) -----

    /// The colour the web view paints before the page has drawn anything, so nothing flashes white. Works before the
    /// page (and, on WebView2, before the controller) exists.
    void SetBackgroundColor(byte red, byte green, byte blue);

    /// The browser profile's preferred colour scheme, which drives the page's own UA styles.
    void SetPreferredColorScheme(AppTheme theme);
}

/// <summary>
/// A live find session: the channel keeps the counters up to date and raises <see cref="CountersChanged"/> on the UI
/// thread. The session dies with the page it was started on.
/// </summary>
public sealed class FindSession
{
    private FindSession(bool countersKnown) => CountersKnown = countersKnown;

    public FindSession()
        : this(countersKnown: true)
    {
    }

    /// Total matches for the term.
    public int MatchCount { get; private set; }

    /// 1-based index of the highlighted match; 0 (or less) means none.
    public int ActiveMatchIndex { get; private set; }

    /// <summary>
    /// False when the platform's find API answers only "found" or "not found" — WebKit's does — so the counters
    /// below carry nothing but that, and the find bar shows no "3/18".
    /// </summary>
    public bool CountersKnown { get; }

    public event EventHandler? CountersChanged;

    /// <summary>A session for a platform with no match counters.</summary>
    public static FindSession WithoutCounters() => new(countersKnown: false);

    /// Called by the channel implementation when the platform reported new counters. UI thread.
    public void UpdateCounters(int matchCount, int activeMatchIndex)
    {
        MatchCount = matchCount;
        ActiveMatchIndex = activeMatchIndex;
        CountersChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The counterless form: all a WebKit find reports is whether the term is on the page. UI thread.</summary>
    public void ReportMatch(bool found) => UpdateCounters(found ? 1 : 0, found ? 1 : 0);
}

/// <summary>
/// The installed web-view runtime has no find API (risk R15). The find bar disables itself for good when it sees this;
/// every other failure is transient and retried on the next input.
/// </summary>
public sealed class FindNotSupportedException : NotSupportedException
{
    public FindNotSupportedException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    public FindNotSupportedException()
        : base("This web view runtime has no find API.")
    {
    }

    public FindNotSupportedException(string message)
        : base(message)
    {
    }
}
