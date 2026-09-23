using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MdReader.Shell.Commands;
using MdReader.Ui.Interop;
using Microsoft.Web.WebView2.Core;

namespace MdReader.Ui.Commands;

/// <summary>
/// The Avalonia half of the §4.12 key routing. It feeds the framework-free <see cref="KeyboardShortcutRouter"/> from
/// the two places a key can arrive: the window's tunnelling key events, and WebView2's
/// <c>AcceleratorKeyPressed</c> for the keys pressed while focus is inside the native web view.
/// </summary>
/// <remarks>
/// The WPF <c>WebView2</c> control republishes AcceleratorKeyPressed as a routed <c>PreviewKeyDown</c> on itself, so
/// the WPF shell only needs the window handler. Avalonia's key events never fire while the native child window has
/// focus, so the accelerator hook is wired explicitly here — and, because that event is raised synchronously with the
/// browser process blocked, everything it triggers is deferred by the shared router (§4.12).
/// </remarks>
public sealed class AvaloniaShortcutRouter
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
    /// A key pressed inside the web view. True means the browser's default must be suppressed
    /// (<c>e.Handled = true</c>); the action itself runs deferred.
    /// </summary>
    public bool HandleAcceleratorKey(CoreWebView2AcceleratorKeyPressedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        ShortcutKey key = ToShortcutKey((int)e.VirtualKey);
        if (key == ShortcutKey.None)
        {
            return false;
        }

        if (e.KeyEventKind is CoreWebView2KeyEventKind.KeyUp or CoreWebView2KeyEventKind.SystemKeyUp)
        {
            _router.HandleKeyUp(key);
            return false;
        }

        // The event carries the key but not the modifier state, so it is read from the keyboard itself.
        return _router.HandleKeyDown(key, CurrentModifiers(), isRepeat: false);
    }

    /// <summary>The modifiers held right now, for a key that arrived without them.</summary>
    public static ShortcutModifiers CurrentModifiers()
    {
        var result = ShortcutModifiers.None;
        if (NativeMethods.IsKeyDown(NativeMethods.VK_MENU))
        {
            result |= ShortcutModifiers.Alt;
        }

        if (NativeMethods.IsKeyDown(NativeMethods.VK_CONTROL))
        {
            result |= ShortcutModifiers.Control;
        }

        if (NativeMethods.IsKeyDown(NativeMethods.VK_SHIFT))
        {
            result |= ShortcutModifiers.Shift;
        }

        if (NativeMethods.IsKeyDown(NativeMethods.VK_LWIN) || NativeMethods.IsKeyDown(NativeMethods.VK_RWIN))
        {
            result |= ShortcutModifiers.Windows;
        }

        return result;
    }

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

    /// The same table by Win32 virtual key, for the keys WebView2 forwards.
    internal static ShortcutKey ToShortcutKey(int virtualKey) => virtualKey switch
    {
        NativeMethods.VK_CONTROL or NativeMethods.VK_SHIFT or NativeMethods.VK_MENU
            or NativeMethods.VK_LWIN or NativeMethods.VK_RWIN => ShortcutKey.Modifier,
        0x42 => ShortcutKey.B,
        0x45 => ShortcutKey.E,
        0x46 => ShortcutKey.F,
        0x4F => ShortcutKey.O,
        0x50 => ShortcutKey.P,
        0x53 => ShortcutKey.S,
        0x57 => ShortcutKey.W,
        0x30 => ShortcutKey.D0,
        0x60 => ShortcutKey.NumPad0,
        0x1B => ShortcutKey.Escape,
        0x09 => ShortcutKey.Tab,
        0x21 => ShortcutKey.PageUp,
        0x22 => ShortcutKey.PageDown,
        0x72 => ShortcutKey.F3,
        0x73 => ShortcutKey.F4,
        0x74 => ShortcutKey.F5,
        0xBB => ShortcutKey.Plus,
        0xBD => ShortcutKey.Minus,
        0x6B => ShortcutKey.NumPadAdd,
        0x6D => ShortcutKey.NumPadSubtract,
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
