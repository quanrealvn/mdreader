using System.Windows;
using System.Windows.Input;
using MdReader.Shell.Commands;

namespace MdReader.App.Commands;

/// <summary>
/// The WPF half of the §4.12 key routing: it installs the tunneling handlers on the main window and translates WPF's
/// <see cref="Key"/> / <see cref="ModifierKeys"/> into the framework-free ones the
/// <see cref="KeyboardShortcutRouter"/> matches. <see cref="Key.System"/> is normalized to <c>e.SystemKey</c>.
/// </summary>
public sealed class WpfShortcutRouter
{
    private readonly KeyboardShortcutRouter _router;

    public WpfShortcutRouter(KeyboardShortcutRouter router)
    {
        ArgumentNullException.ThrowIfNull(router);
        _router = router;
    }

    /// Installs the router on the window (once).
    public void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.PreviewKeyDown += OnPreviewKeyDown;
        window.PreviewKeyUp += OnPreviewKeyUp;
        window.Deactivated += (_, _) => _router.Reset();
    }

    /// The §4.12 keys, by their WPF name; everything else is <see cref="ShortcutKey.None"/>.
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

    internal static ShortcutModifiers ToShortcutModifiers(ModifierKeys modifiers)
    {
        var result = ShortcutModifiers.None;
        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            result |= ShortcutModifiers.Alt;
        }

        if ((modifiers & ModifierKeys.Control) != 0)
        {
            result |= ShortcutModifiers.Control;
        }

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            result |= ShortcutModifiers.Shift;
        }

        if ((modifiers & ModifierKeys.Windows) != 0)
        {
            result |= ShortcutModifiers.Windows;
        }

        return result;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled)
        {
            return;
        }

        var key = ToShortcutKey(e.Key == Key.System ? e.SystemKey : e.Key);
        if (key == ShortcutKey.None)
        {
            return;
        }

        if (_router.HandleKeyDown(key, ToShortcutModifiers(e.KeyboardDevice.Modifiers), e.IsRepeat))
        {
            e.Handled = true;
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e) =>
        _router.HandleKeyUp(ToShortcutKey(e.Key == Key.System ? e.SystemKey : e.Key));
}
