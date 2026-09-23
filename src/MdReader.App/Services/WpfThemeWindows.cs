using System.Collections;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using MdReader.App.Interop;
using MdReader.Core.Theming;
using MdReader.Shell.Services;

namespace MdReader.App.Services;

/// <summary>
/// The WPF half of the theme (§10): the palette dictionary at index 0 of <c>Application.Resources.MergedDictionaries</c>
/// and the DWM title bar of every attached window. <see cref="ThemeService"/> owns the decision; this owns the paint.
/// </summary>
/// <remarks>
/// Constructed on the UI thread after the App resources are loaded and before the first window is built: the palette is
/// applied in the constructor, so no window ever renders with the wrong colours.
/// </remarks>
public sealed class WpfThemeWindows : IDisposable
{
    private const int DwmColorDefault = unchecked((int)0xFFFFFFFF);

    private static readonly Uri LightPaletteUri = new("/MdReader;component/Themes/Light.xaml", UriKind.Relative);
    private static readonly Uri DarkPaletteUri = new("/MdReader;component/Themes/Dark.xaml", UriKind.Relative);

    private readonly IThemeService _theme;
    private readonly List<Window> _windows = [];

    private ResourceDictionary? _lightPalette;
    private ResourceDictionary? _darkPalette;
    private ResourceDictionary? _appliedPalette;
    private bool _disposed;

    public WpfThemeWindows(IThemeService theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        _theme = theme;
        ApplyPalette();
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
        window.SourceInitialized += (_, _) => ApplyWindowFrame(window);
        window.Closed += (_, _) => _windows.Remove(window);
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            ApplyWindowFrame(window);
        }
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

        ApplyPalette();
        foreach (var window in _windows.ToArray())
        {
            ApplyWindowFrame(window);
        }
    }

    private void ApplyPalette()
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        // App.xaml merges only Controls.xaml; the first call inserts the palette at index 0 (so only the palette that's actually
        // used gets parsed), later calls swap it there.
        var merged = resources.MergedDictionaries;
        if (_appliedPalette is null && merged.Count > 0 && IsPalette(merged[0], "Light.xaml"))
        {
            _lightPalette = merged[0];   // tolerate an App.xaml that still merges a palette itself
            _appliedPalette = merged[0];
        }

        var palette = _theme.IsHighContrast
            ? BuildHighContrastPalette(_lightPalette ??= LoadPalette(LightPaletteUri))
            : _theme.EffectiveTheme == AppTheme.Dark
                ? _darkPalette ??= LoadPalette(DarkPaletteUri)
                : _lightPalette ??= LoadPalette(LightPaletteUri);

        if (_appliedPalette is null)
        {
            merged.Insert(0, palette);
        }
        else if (!ReferenceEquals(merged[0], palette))
        {
            merged[0] = palette;
        }

        _appliedPalette = palette;
    }

    private void ApplyWindowFrame(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var highContrast = _theme.IsHighContrast;
        var useDark = _theme.EffectiveTheme == AppTheme.Dark && !highContrast ? 1 : 0;
        if (NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, in useDark, sizeof(int)) != 0)
        {
            // Windows 10 before 20H1 used attribute 19.
            NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, in useDark, sizeof(int));
        }

        if (Environment.OSVersion.Version.Build < 22000)
        {
            return;
        }

        var caption = DwmColorDefault;
        var text = DwmColorDefault;
        if (!highContrast && _appliedPalette is not null)
        {
            caption = ToColorRef(_appliedPalette, "Brush.Chrome.Background") ?? DwmColorDefault;
            text = ToColorRef(_appliedPalette, "Brush.Foreground") ?? DwmColorDefault;
        }

        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_CAPTION_COLOR, in caption, sizeof(int));
        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_TEXT_COLOR, in text, sizeof(int));
    }

    private static ResourceDictionary LoadPalette(Uri source) => new() { Source = source };

    private static bool IsPalette(ResourceDictionary dictionary, string fileName) =>
        dictionary.Source?.OriginalString.EndsWith(fileName, StringComparison.OrdinalIgnoreCase) == true;

    private static int? ToColorRef(ResourceDictionary palette, string key) =>
        palette[key] is SolidColorBrush brush ? NativeMethods.ToColorRef(brush.Color.R, brush.Color.G, brush.Color.B) : null;

    /// Best effort for Windows high-contrast themes (§1.2): every palette key maps to a system brush.
    private static ResourceDictionary BuildHighContrastPalette(ResourceDictionary template)
    {
        var palette = new ResourceDictionary();
        foreach (DictionaryEntry entry in template)
        {
            var key = (string)entry.Key;
            palette[key] = entry.Value is Color ? SystemColors.WindowTextColor : HighContrastBrush(key);
        }

        return palette;
    }

    private static Brush HighContrastBrush(string key) => key switch
    {
        "Brush.Document.Background" or "Brush.Tab.Active.Background" or "Brush.Menu.Background" or "Brush.ToolTip.Background"
            or "Brush.Input.Background" or "Brush.FindBar.Background" or "Brush.Illustration.Background" => SystemColors.WindowBrush,
        "Brush.Chrome.Background" => SystemColors.ControlBrush,
        "Brush.Foreground" or "Brush.Border" or "Brush.Menu.Border" or "Brush.Warning" or "Brush.Danger"
            or "Brush.ScrollBar.Thumb" or "Brush.ScrollBar.Thumb.Hover" => SystemColors.WindowTextBrush,
        "Brush.Foreground.Muted" or "Brush.Foreground.Disabled" or "Brush.Border.Subtle" => SystemColors.GrayTextBrush,
        "Brush.Accent" or "Brush.Accent.Emphasis" or "Brush.Accent.Emphasis.Hover" or "Brush.Accent.Emphasis.Pressed"
            or "Brush.Toast.Background" => SystemColors.HighlightBrush,
        "Brush.Accent.Foreground" or "Brush.Toast.Foreground" => SystemColors.HighlightTextBrush,
        "Brush.Tab.Hover.Background" or "Brush.Button.Hover.Background" or "Brush.Button.Pressed.Background"
            or "Brush.Button.Checked.Background" => SystemColors.ControlLightBrush,
        _ => SystemColors.WindowBrush,
    };
}
