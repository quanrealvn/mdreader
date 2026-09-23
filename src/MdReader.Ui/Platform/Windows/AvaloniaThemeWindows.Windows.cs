using Avalonia.Controls;
using Avalonia.Media;
using MdReader.Core.Theming;
using MdReader.Ui.Interop;

namespace MdReader.Ui.Services;

/// <summary>
/// The Windows half of the theme (§10, step 2): DWM's immersive dark mode for every attached window and, on
/// Windows 11, the caption and text colours taken from the palette in force.
/// </summary>
public sealed partial class AvaloniaThemeWindows
{
    private const int DwmColorDefault = unchecked((int)0xFFFFFFFF);

    private partial void ApplyWindowFrame(Window window)
    {
        nint handle = window.TryGetPlatformHandle()?.Handle ?? 0;
        if (handle == 0)
        {
            return;
        }

        bool highContrast = _theme.IsHighContrast;
        int useDark = _theme.EffectiveTheme == AppTheme.Dark && !highContrast ? 1 : 0;
        if (NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, in useDark, sizeof(int)) != 0)
        {
            // Windows 10 before 20H1 used attribute 19.
            NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, in useDark, sizeof(int));
        }

        if (Environment.OSVersion.Version.Build < 22000)
        {
            return;
        }

        int caption = DwmColorDefault;
        int text = DwmColorDefault;
        if (!highContrast)
        {
            caption = ToColorRef("Brush.Chrome.Background") ?? DwmColorDefault;
            text = ToColorRef("Brush.Foreground") ?? DwmColorDefault;
        }

        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_CAPTION_COLOR, in caption, sizeof(int));
        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_TEXT_COLOR, in text, sizeof(int));
    }

    private int? ToColorRef(string key) =>
        PaletteColor(key) is { } color ? NativeMethods.ToColorRef(color.R, color.G, color.B) : null;
}
