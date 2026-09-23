namespace MdReader.Mac;

/// <summary>
/// The per-shell half of one WKWebView. Everything <see cref="WkWebViewChannel"/> does lives on the web view itself
/// and is the same in every shell; these few things do not, because they depend on the view the shell has put in its
/// window. It is the same split the Windows backend makes with <c>IWebView2Surface</c>.
/// </summary>
/// <remarks>All members are UI-thread only: WebKit and AppKit are main-thread only.</remarks>
public interface IWkWebViewSurface
{
    /// <summary>
    /// The <c>NSView</c> the web view is planted in, once the shell has one. On the Avalonia shell that is the
    /// native control host's view, which exists as soon as the document view joins the window.
    /// </summary>
    Task<nint> GetContainerAsync();

    /// <summary>
    /// Hands the shell the web view the channel created, for it to add and size. Called once, on the container the
    /// previous call returned.
    /// </summary>
    void AttachWebView(nint webView);

    /// <summary>The web view's <c>pageZoom</c>. Settable before the view exists; applied when it is attached.</summary>
    double ZoomFactor { get; set; }

    /// <summary>
    /// The user zoomed inside the page. WKWebView has no page-zoom gesture and reports no such change, so on macOS
    /// this never fires; it exists so the channel reads the way the Windows one does.
    /// </summary>
    event EventHandler? ZoomFactorChanged;

    /// <summary>
    /// The colour the view paints before the page has drawn anything (§10). Must work before the web view exists:
    /// the container carries it until there is one.
    /// </summary>
    void SetDefaultBackgroundColor(byte red, byte green, byte blue);

    /// <summary>Moves keyboard focus into the page.</summary>
    void FocusWebView();
}
