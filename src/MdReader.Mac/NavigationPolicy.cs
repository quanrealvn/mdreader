using MdReader.Shell.Documents;

namespace MdReader.Mac;

/// <summary>What the web view is allowed to do with a navigation it asked about.</summary>
public enum NavigationOutcome
{
    /// The viewer page itself: let it load.
    Allow,

    /// Cancel, and say nothing more about it.
    Cancel,

    /// Cancel, and hand the URL to the link classifier as though the page had reported a click.
    CancelAndRouteLink,
}

/// <summary>A decision plus, for <see cref="NavigationOutcome.CancelAndRouteLink"/>, what to route.</summary>
public readonly record struct NavigationDecision(NavigationOutcome Outcome, Uri? Link = null, bool NewTab = false)
{
    public static readonly NavigationDecision Allow = new(NavigationOutcome.Allow);

    public static readonly NavigationDecision Cancel = new(NavigationOutcome.Cancel);

    public static NavigationDecision Route(Uri link, bool newTab) => new(NavigationOutcome.CancelAndRouteLink, link, newTab);
}

/// <summary>
/// The navigation half of ARCHITECTURE §8.4 for WKWebView: the same allow-list, the same fallback routing and the
/// same "no popups" rule that <c>WebViewSecurity</c>'s <c>NavigationStarting</c> and <c>NewWindowRequested</c>
/// handlers apply on Windows, written as a pure function so both can be tested against the same table.
/// </summary>
/// <remarks>
/// WKWebView has no <c>IsUserInitiated</c>. The nearest thing is the navigation type, so a navigation counts as user
/// initiated only when it is an activated link or a submitted form — which is exactly the set WebView2 reports as
/// user initiated for content that isn't scripted. Everything else (scripted location changes, reloads, back/forward,
/// "other") is cancelled without being routed, which is the safe direction: a document can't make the shell open
/// anything by setting <c>location</c>.
/// </remarks>
public static class NavigationPolicy
{
    /// <summary>The <c>WKNavigationType</c> values this policy distinguishes.</summary>
    public enum NavigationType
    {
        LinkActivated = 0,
        FormSubmitted = 1,
        BackForward = 2,
        Reload = 3,
        FormResubmitted = 4,
        Other = -1,
    }

    /// <summary><c>webView:decidePolicyForNavigationAction:decisionHandler:</c>.</summary>
    /// <param name="isMainFrame">False for a subframe: those are always cancelled, as <c>FrameNavigationStarting</c>
    /// cancels them on Windows.</param>
    public static NavigationDecision ForNavigation(string? url, NavigationType type, bool isMainFrame, bool newTab)
    {
        if (!isMainFrame)
        {
            return NavigationDecision.Cancel;
        }

        if (PageOrigin.IsPageUrl(url))
        {
            return NavigationDecision.Allow;
        }

        return RouteIfUserInitiated(url, IsUserInitiated(type), newTab);
    }

    /// <summary><c>webView:createWebViewWithConfiguration:forNavigationAction:windowFeatures:</c>: never a popup, and
    /// a link the user activated opens in a new tab (activate = false), exactly as on Windows.</summary>
    public static NavigationDecision ForNewWindow(string? url, NavigationType type) =>
        RouteIfUserInitiated(url, IsUserInitiated(type), newTab: true);

    /// <summary>
    /// <c>webView:decidePolicyForNavigationResponse:decisionHandler:</c>. Only a response the page itself asked for
    /// may be shown, and only when WebKit can display it: anything else would become a download.
    /// </summary>
    public static bool AllowResponse(string? url, bool canShowMimeType) => canShowMimeType && PageOrigin.IsPageUrl(url);

    private static NavigationDecision RouteIfUserInitiated(string? url, bool userInitiated, bool newTab)
    {
        if (!userInitiated || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return NavigationDecision.Cancel;
        }

        // file: = a file dropped before our own drop handler saw it, or a link the page script missed; http(s) = a
        // click the page missed. Both go through the LinkClassifier, which gates the path lexically before any I/O.
        return uri.IsFile || IsHttp(uri) ? NavigationDecision.Route(uri, newTab) : NavigationDecision.Cancel;
    }

    private static bool IsUserInitiated(NavigationType type) =>
        type is NavigationType.LinkActivated or NavigationType.FormSubmitted;

    private static bool IsHttp(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
        || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
}
