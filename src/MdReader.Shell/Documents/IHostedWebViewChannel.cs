namespace MdReader.Shell.Documents;

/// <summary>
/// <see cref="IWebViewChannel"/> plus the three things a document view needs from the web view it owns, rather than
/// from the page: whether the view can still be shown, how to try again, and when it has given up.
/// </summary>
/// <remarks>
/// <see cref="IWebViewChannel"/> is what <c>DocumentSession</c> talks to and is deliberately free of lifetime; this is
/// what the view talks to, so a shell can drive either backend without naming WebView2 or WKWebView.
/// </remarks>
public interface IHostedWebViewChannel : IWebViewChannel, IDisposable
{
    /// <summary>The page can't be shown at all: the view replaces the web view with its own error.</summary>
    event EventHandler<WebViewHostFailedEventArgs>? HostFailed;

    /// <summary>True when <see cref="Restart"/> can recover; false when only a new web view can.</summary>
    bool CanRestart { get; }

    /// <summary>Clears the failure history and reloads the page (the shell error's "Try again").</summary>
    void Restart();
}
