using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using MdReader.Core.Theming;
using MdReader.Shell.Services;

namespace MdReader.Ui.Services;

/// <summary>
/// The Avalonia half of the theme (§10): the application's <see cref="ThemeVariant"/> and the window frame of every
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
public sealed partial class AvaloniaThemeWindows : IDisposable
{
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

    /// <summary>
    /// The window's own frame. On Windows that is DWM's dark mode and, on Windows 11, the caption and text colours;
    /// on macOS the title bar follows the window's <c>NSAppearance</c>, which the variant above already sets.
    /// </summary>
    private partial void ApplyWindowFrame(Window window);

    /// Reads the palette brush for the variant in force, so a title bar never has its own copy of the colours.
    private Color? PaletteColor(string key) =>
        Application.Current?.TryFindResource(key, Variant, out object? value) == true
        && value is ISolidColorBrush { Color: { } color }
            ? color
            : null;
}
