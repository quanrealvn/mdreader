using Microsoft.Web.WebView2.Core;

namespace MdReader.Edge;

/// <summary>
/// The per-shell half of one WebView2 view. Everything <see cref="WebView2Channel"/> does lives on
/// <see cref="CoreWebView2"/> and is identical in every shell; these few settings do not, because they live on the WPF
/// <c>WebView2</c> control or on the <c>CoreWebView2Controller</c> the Avalonia shell owns directly.
/// </summary>
/// <remarks>All members are UI-thread only.</remarks>
public interface IWebView2Surface
{
    /// <summary>
    /// Creates the view's <see cref="CoreWebView2"/> (or returns the one the shell already made) and applies the
    /// settings that belong to the surface, such as accepting external drops. Called once per surface.
    /// </summary>
    Task<CoreWebView2> EnsureCoreWebView2Async(CoreWebView2Environment environment);

    double ZoomFactor { get; set; }

    /// <summary>The user zoomed inside the page (Ctrl+wheel).</summary>
    event EventHandler? ZoomFactorChanged;

    /// <summary>
    /// The colour the view paints before the page has drawn anything (§10). Must work before the core exists: the shell
    /// either carries the value over to the controller or holds it until there is one.
    /// </summary>
    void SetDefaultBackgroundColor(byte red, byte green, byte blue);
}
