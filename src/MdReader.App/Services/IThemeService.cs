using MdReader.Core.Settings;
using MdReader.Core.Theming;

namespace MdReader.App.Services;

public interface IThemeService
{
    ThemePreference Preference { get; }
    AppTheme EffectiveTheme { get; }
    event EventHandler? EffectiveThemeChanged;              // UI thread
    void SetPreference(ThemePreference preference);          // persists via SettingsCoordinator
    void ApplySessionOverride(ThemePreference preference);   // --theme; not persisted
    void AttachWindow(System.Windows.Window window);         // keeps DWM title bar in sync
}
