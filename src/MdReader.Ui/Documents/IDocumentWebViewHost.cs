using Avalonia.Controls;
using MdReader.Shell.Commands;
using MdReader.Shell.Documents;
using MdReader.Shell.ViewModels;

namespace MdReader.Ui.Documents;

/// <summary>
/// One tab's web view, as the document view sees it: a control to put in the visual tree, a channel to talk through,
/// and the keys the native view swallowed on their way to the page.
/// </summary>
/// <remarks>
/// This is the whole of what differs between the two backends inside the Avalonia shell. On Windows it wraps the
/// native host window and <c>WebView2Channel</c>; on macOS an <c>NSView</c> and <c>WkWebViewChannel</c>. Everything
/// above it — the find bar, the editor pane, zoom, theme, the error panel and the recovery states — is one file for
/// both. All members are UI-thread only.
/// </remarks>
internal interface IDocumentWebViewHost : IDisposable
{
    /// <summary>The control the document view adds to its web-view cell. Created once and never re-parented.</summary>
    Control Control { get; }

    /// <summary>
    /// Every key the page sees, before the page sees it. This is the only route by which keystrokes typed inside the
    /// native web view reach the shell: Avalonia's own key events never fire while a native view has focus. On
    /// Windows it is raised synchronously with the browser process blocked, so handlers must defer their work
    /// (§4.12); on macOS the menu bar dispatches the same shortcuts before the page sees them, so it is never raised.
    /// </summary>
    event EventHandler<ForwardedKeyEventArgs>? AcceleratorKeyPressed;

    /// <summary>Keyboard focus moved into the native web view; the print-mode fallback (R6) needs it.</summary>
    event EventHandler? WebViewFocused;

    /// <summary>Creates the channel for this host. One host may create several over its life (recovery).</summary>
    IHostedWebViewChannel CreateChannel();

    /// <summary>
    /// Brings <paramref name="channel"/> up: waits for the platform's environment, creates the native web view and
    /// navigates to the viewer page. Throws if any of that fails, which the view turns into its error panel.
    /// </summary>
    Task InitializeAsync(IHostedWebViewChannel channel, string resourceRoot);

    /// <summary>Tears the native web view down but keeps the control, so a replacement can be built in place.</summary>
    void DestroyWebView();

    /// <summary>Moves keyboard focus into the page. Never called in capture mode (it would activate the window).</summary>
    void FocusWebView();

    /// <summary>The tab is closing: tear the native side down for good.</summary>
    void Shutdown();
}

/// <summary>A key the native web view forwarded, in the shell's own vocabulary (§4.12).</summary>
/// <remarks>
/// The backends report keys in their own terms — WebView2 a Win32 virtual key and a key-event kind — and translate
/// here, so the router and the document view never learn either vocabulary.
/// </remarks>
internal sealed class ForwardedKeyEventArgs(ShortcutKey key, bool isKeyUp) : EventArgs
{
    public ShortcutKey Key { get; } = key;

    public bool IsKeyUp { get; } = isKeyUp;

    /// <summary>Set by a handler that took the key, so the browser's own default is suppressed.</summary>
    public bool Handled { get; set; }
}

/// <summary>Builds the host for whichever backend this build has. One line, two implementations.</summary>
internal static partial class DocumentWebViewHostFactory
{
    internal static partial IDocumentWebViewHost Create(DocumentViewServices services);
}
