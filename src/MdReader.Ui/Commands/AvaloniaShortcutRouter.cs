using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MdReader.Shell.Commands;
using MdReader.Ui.Documents;

namespace MdReader.Ui.Commands;

/// <summary>
/// The Avalonia half of the §4.12 key routing. It feeds the framework-free <see cref="KeyboardShortcutRouter"/> from
/// the two places a key can arrive: the window's tunnelling key events, and the keys the native web view forwards
/// when focus is inside the page.
/// </summary>
/// <remarks>
/// The WPF <c>WebView2</c> control republishes AcceleratorKeyPressed as a routed <c>PreviewKeyDown</c> on itself, so
/// the WPF shell only needs the window handler. Avalonia's key events never fire while a native child view has focus,
/// so the forwarding hook is wired explicitly here — and, because WebView2 raises it synchronously with the browser
/// process blocked, everything it triggers is deferred by the shared router (§4.12). On macOS nothing arrives that
/// way: the native menu bar dispatches every shortcut before the page's first responder sees the key.
/// </remarks>
public sealed partial class AvaloniaShortcutRouter
{
    private readonly KeyboardShortcutRouter _router;

    public AvaloniaShortcutRouter(KeyboardShortcutRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        _router = router;
    }

    /// Installs the router on the window (once).
    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.AddHandler(InputElement.KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        window.AddHandler(InputElement.KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);
        window.Deactivated += (_, _) => _router.Reset();
    }

    /// <summary>
    /// A key the native web view forwarded. True means the browser's default must be suppressed; the action itself
    /// runs deferred.
    /// </summary>
    internal bool HandleForwardedKey(ForwardedKeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.Key == ShortcutKey.None)
        {
            return false;
        }

        if (e.IsKeyUp)
        {
            _router.HandleKeyUp(e.Key);
            return false;
        }

        // A forwarded key carries no modifier state, so it is read from the keyboard itself.
        return _router.HandleKeyDown(e.Key, CurrentModifiers(), isRepeat: false);
    }

    /// <summary>The modifiers held right now, for a key that arrived without them.</summary>
    public static partial ShortcutModifiers CurrentModifiers();

    /// The §4.12 keys, by their Avalonia name; everything else is <see cref="ShortcutKey.None"/>.
    internal static ShortcutKey ToShortcutKey(Key key) => key switch
    {
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
            or Key.LWin or Key.RWin => ShortcutKey.Modifier,
        Key.B => ShortcutKey.B,
        Key.E => ShortcutKey.E,
        Key.F => ShortcutKey.F,
        Key.O => ShortcutKey.O,
        Key.P => ShortcutKey.P,
        Key.S => ShortcutKey.S,
        Key.W => ShortcutKey.W,
        Key.D0 => ShortcutKey.D0,
        Key.NumPad0 => ShortcutKey.NumPad0,
        Key.Escape => ShortcutKey.Escape,
        Key.Tab => ShortcutKey.Tab,
        Key.PageUp => ShortcutKey.PageUp,
        Key.PageDown => ShortcutKey.PageDown,
        Key.F3 => ShortcutKey.F3,
        Key.F4 => ShortcutKey.F4,
        Key.F5 => ShortcutKey.F5,
        Key.OemPlus => ShortcutKey.Plus,
        Key.OemMinus => ShortcutKey.Minus,
        Key.Add => ShortcutKey.NumPadAdd,
        Key.Subtract => ShortcutKey.NumPadSubtract,
        _ => ShortcutKey.None,
    };

    internal static ShortcutModifiers ToShortcutModifiers(KeyModifiers modifiers)
    {
        var result = ShortcutModifiers.None;
        if ((modifiers & KeyModifiers.Alt) != 0)
        {
            result |= ShortcutModifiers.Alt;
        }

        if ((modifiers & KeyModifiers.Control) != 0)
        {
            result |= ShortcutModifiers.Control;
        }

        if ((modifiers & KeyModifiers.Shift) != 0)
        {
            result |= ShortcutModifiers.Shift;
        }

        if ((modifiers & KeyModifiers.Meta) != 0)
        {
            result |= ShortcutModifiers.Windows;
        }

        return result;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        ShortcutKey key = ToShortcutKey(e.Key);
        if (key == ShortcutKey.None)
        {
            return;
        }

        // Avalonia doesn't report auto-repeat, so the router's "still held since the last handled press" rule decides,
        // exactly as it does for the keys WebView2 forwards.
        if (_router.HandleKeyDown(key, ToShortcutModifiers(e.KeyModifiers), isRepeat: false))
        {
            e.Handled = true;
        }
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e) => _router.HandleKeyUp(ToShortcutKey(e.Key));
}
