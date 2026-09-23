using MdReader.Core.Settings;
using MdReader.Core.Theming;

namespace MdReader.Shell.Services;

public interface IThemeService
{
    ThemePreference Preference { get; }
    AppTheme EffectiveTheme { get; }

    /// The OS is in a high-contrast theme: the shell paints from system colours instead of the palette (§1.2).
    bool IsHighContrast { get; }

    /// Raised before <see cref="EffectiveThemeChanged"/> whenever the palette to paint with changed (theme or high
    /// contrast), so the shell swaps its resources and window frames before anything else reacts.
    event EventHandler? PaletteChanged;                      // UI thread

    event EventHandler? EffectiveThemeChanged;               // UI thread
    void SetPreference(ThemePreference preference);          // persists via SettingsCoordinator
    void ApplySessionOverride(ThemePreference preference);   // --theme; not persisted
}

/// <summary>
/// The OS light/dark and high-contrast settings, read per shell (Windows: the Personalize registry key plus
/// <c>SystemParameters.HighContrast</c>; macOS: <c>NSApp.effectiveAppearance</c>). Injected into
/// <see cref="ThemeService"/> so the shared service never touches the platform.
/// </summary>
public interface ISystemThemeProbe
{
    /// False only when the OS is explicitly in dark mode; a missing or unreadable setting means light.
    bool AppsUseLightTheme { get; }

    bool IsHighContrast { get; }

    /// Raised (on any thread) when one of the values above may have changed.
    event EventHandler? Changed;
}
