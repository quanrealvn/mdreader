using MdReader.Core.Diagnostics;
using MdReader.Core.Settings;
using MdReader.Core.Theming;
using MdReader.Shell.Threading;

namespace MdReader.Shell.Services;

/// <summary>
/// Source of truth for the effective theme (§10), with no UI framework in sight:
/// EffectiveTheme = ThemeResolver.Resolve(sessionOverride ?? settings.Theme, probe.AppsUseLightTheme).
/// On change it raises <see cref="PaletteChanged"/> first — the shell's window adapter swaps its dictionaries and
/// repaints the window frames there — and then <see cref="EffectiveThemeChanged"/>, which the tabs follow.
/// </summary>
public sealed class ThemeService : IThemeService, IDisposable
{
    private const string Category = "Theme";

    private readonly SettingsCoordinator _settings;
    private readonly ISystemThemeProbe _probe;
    private readonly IAppLog _log;
    private readonly IUiDispatcher _dispatcher;

    private ThemePreference? _sessionOverride;
    private ThemePreference _lastSettingsTheme;
    private bool _systemUsesLightTheme;
    private bool _highContrast;
    private AppTheme _effectiveTheme;
    private bool _disposed;

    /// Must be constructed on the UI thread, before the first window is built: the shell's window adapter applies the
    /// initial palette from <see cref="EffectiveTheme"/> so no window ever renders with the wrong colours.
    public ThemeService(SettingsCoordinator settings, ISystemThemeProbe probe, IAppLog log, IUiDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _settings = settings;
        _probe = probe;
        _log = log;
        _dispatcher = dispatcher;
        _lastSettingsTheme = settings.Current.Theme;
        _systemUsesLightTheme = probe.AppsUseLightTheme;
        _highContrast = probe.IsHighContrast;
        _effectiveTheme = ThemeResolver.Resolve(Preference, _systemUsesLightTheme);

        _log.Write(AppLogLevel.Info, Category,
            $"Initial preference {Preference}, system {(_systemUsesLightTheme ? "light" : "dark")}, effective {_effectiveTheme}"
            + (_highContrast ? ", high contrast" : ""));

        _probe.Changed += OnSystemThemeChanged;
        _settings.Changed += OnSettingsChanged;
    }

    public ThemePreference Preference => _sessionOverride ?? _settings.Current.Theme;

    public AppTheme EffectiveTheme => _effectiveTheme;

    public bool IsHighContrast => _highContrast;

    public event EventHandler? PaletteChanged;

    public event EventHandler? EffectiveThemeChanged;

    public void SetPreference(ThemePreference preference)
    {
        _dispatcher.VerifyAccess();
        _sessionOverride = null;
        _settings.Update(s => s with { Theme = preference });
        Reevaluate();
    }

    public void ApplySessionOverride(ThemePreference preference)
    {
        _dispatcher.VerifyAccess();
        _sessionOverride = preference;
        Reevaluate();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _probe.Changed -= OnSystemThemeChanged;
        _settings.Changed -= OnSettingsChanged;
    }

    private void Reevaluate()
    {
        if (_disposed)
        {
            return;
        }

        var effective = ThemeResolver.Resolve(Preference, _systemUsesLightTheme);
        var highContrast = _probe.IsHighContrast;
        var themeChanged = effective != _effectiveTheme;
        var paletteChanged = themeChanged || highContrast != _highContrast;
        _effectiveTheme = effective;
        _highContrast = highContrast;

        if (paletteChanged)
        {
            PaletteChanged?.Invoke(this, EventArgs.Empty);
        }

        if (themeChanged)
        {
            _log.Write(AppLogLevel.Info, Category, $"Effective theme changed to {effective} (preference {Preference})");
            EffectiveThemeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnSystemThemeChanged(object? sender, EventArgs e)
    {
        // Raised on the OS notification thread (or ours); re-reading the probe is cheap, so re-evaluate on every change.
        _dispatcher.Post(() =>
        {
            _systemUsesLightTheme = _probe.AppsUseLightTheme;
            Reevaluate();
        });
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        if (settings.Theme == _lastSettingsTheme)
        {
            return;
        }

        _lastSettingsTheme = settings.Theme;
        if (_dispatcher.CheckAccess())
        {
            Reevaluate();
        }
        else
        {
            _dispatcher.Post(Reevaluate);
        }
    }
}
