using MdReader.Shell.Commands;
using MdReader.Ui.Interop;

namespace MdReader.Ui.Commands;

/// <summary>
/// The Windows half of the key routing: the §4.12 table by Win32 virtual key, for the keys WebView2 forwards, and
/// the modifier state read from the keyboard, because a forwarded key doesn't carry it.
/// </summary>
public sealed partial class AvaloniaShortcutRouter
{
    /// The same table as <see cref="ToShortcutKey(Avalonia.Input.Key)"/>, by Win32 virtual key.
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

    public static partial ShortcutModifiers CurrentModifiers()
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
}
