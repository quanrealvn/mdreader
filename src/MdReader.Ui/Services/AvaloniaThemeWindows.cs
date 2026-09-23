using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using MdReader.Core.Theming;
using MdReader.Shell.Services;
using MdReader.Ui.Interop;

namespace MdReader.Ui.Services;

/// <summary>
/// The Avalonia half of the theme (§10): the application's <see cref="ThemeVariant"/> and the DWM title bar of every
/// attached window. <see cref="ThemeService"/> owns the decision; this owns the paint.
/// </summary>
/// <remarks>
/// <para>
/// WPF swaps a palette dictionary at index 0 of <c>Application.Resources.MergedDictionaries</c>. Avalonia has the same
/// idea built in: Themes/Palette.axaml declares both palettes as theme dictionaries and every chrome brush is a
/// DynamicResource, so setting the variant repaints the whole shell (and the Fluent controls with it).
/// </para>
/// <para>
/// Constructed on the UI thread before the first window is built, so no window ever renders with the wrong colours.
/// </para>
/// </remarks>
public sealed class AvaloniaThemeWindows : IDisposable
{
    private const int DwmColorDefault = unchecked((int)0xFFFFFFFF);

    private readonly IThemeService _theme;
    private readonly List<Window> _windows = [];
    private bool _disposed;

    public AvaloniaThemeWindows(IThemeService theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        _theme = theme;
        ApplyVariant();
        _theme.PaletteChanged += OnPaletteChanged;
    }

    /// Keeps a window's DWM title bar in sync with the effective theme. UI thread.
    public void AttachWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_windows.Contains(window))
        {
            return;
        }

        _windows.Add(window);
        window.Opened += (_, _) => ApplyWindowFrame(window);
        window.Closed += (_, _) => _windows.Remove(window);
        ApplyWindowFrame(window);   // a window that already has its handle (Avalonia creates it in the constructor)
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _theme.PaletteChanged -= OnPaletteChanged;
    }

    private void OnPaletteChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        ApplyVariant();
        foreach (Window window in _windows.ToArray())
        {
            ApplyWindowFrame(window);
        }
    }

    private void ApplyVariant()
    {
        if (Application.Current is { } application)
        {
            application.RequestedThemeVariant = Variant;
        }
    }

    private ThemeVariant Variant => _theme.EffectiveTheme == AppTheme.Dark ? ThemeVariant.Dark : ThemeVariant.Light;

    private void ApplyWindowFrame(Window window)
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

    /// Reads the palette brush for the variant in force, so the title bar never has its own copy of the colours.
    private int? ToColorRef(string key)
    {
        if (Application.Current?.TryFindResource(key, Variant, out object? value) == true
            && value is ISolidColorBrush { Color: { } color })
        {
            return NativeMethods.ToColorRef(color.R, color.G, color.B);
        }

        return null;
    }
}
