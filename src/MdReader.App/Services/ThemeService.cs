using System.Collections;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using MdReader.App.Interop;
using MdReader.Core.Diagnostics;
using MdReader.Core.Settings;
using MdReader.Core.Theming;
using Microsoft.Win32;

namespace MdReader.App.Services;

/// Source of truth for the effective theme (§10). EffectiveTheme = ThemeResolver.Resolve(sessionOverride ?? settings.Theme,
/// AppsUseLightTheme). On change: swap the WPF palette (index 0 of Application.Resources.MergedDictionaries), update the DWM
/// title bar of every attached window, then raise EffectiveThemeChanged (tabs update their WebViews).
public sealed class ThemeService : IThemeService, IDisposable
{
    private const string Category = "Theme";
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const int DwmColorDefault = unchecked((int)0xFFFFFFFF);

    private static readonly Uri LightPaletteUri = new("/MdReader;component/Themes/Light.xaml", UriKind.Relative);
    private static readonly Uri DarkPaletteUri = new("/MdReader;component/Themes/Dark.xaml", UriKind.Relative);

    private readonly SettingsCoordinator _settings;
    private readonly IAppLog _log;
    private readonly Dispatcher _dispatcher;
    private readonly List<Window> _windows = [];

    private ThemePreference? _sessionOverride;
    private ThemePreference _lastSettingsTheme;
    private bool _systemUsesLightTheme;
    private bool _highContrast;
    private AppTheme _effectiveTheme;
    private ResourceDictionary? _lightPalette;
    private ResourceDictionary? _darkPalette;
    private ResourceDictionary? _appliedPalette;
    private bool _disposed;

    /// Must be constructed on the UI thread after the App resources are loaded; applies the initial palette immediately so
    /// the main window never renders with the wrong colors.
    public ThemeService(SettingsCoordinator settings, IAppLog log)
    {
        _settings = settings;
        _log = log;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _lastSettingsTheme = settings.Current.Theme;
        _systemUsesLightTheme = ReadAppsUseLightTheme();
        _highContrast = SystemParameters.HighContrast;
        _effectiveTheme = ThemeResolver.Resolve(Preference, _systemUsesLightTheme);

        ApplyPalette();
        _log.Write(AppLogLevel.Info, Category,
            $"Initial preference {Preference}, system {(_systemUsesLightTheme ? "light" : "dark")}, effective {_effectiveTheme}"
            + (_highContrast ? ", high contrast" : ""));

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _settings.Changed += OnSettingsChanged;
    }

    public ThemePreference Preference => _sessionOverride ?? _settings.Current.Theme;

    public AppTheme EffectiveTheme => _effectiveTheme;

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

    public void AttachWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _dispatcher.VerifyAccess();
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
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _settings.Changed -= OnSettingsChanged;
    }

    private void Reevaluate()
    {
        if (_disposed)
        {
            return;
        }

        var effective = ThemeResolver.Resolve(Preference, _systemUsesLightTheme);
        var highContrast = SystemParameters.HighContrast;
        var themeChanged = effective != _effectiveTheme;
        var paletteChanged = themeChanged || highContrast != _highContrast;
        _effectiveTheme = effective;
        _highContrast = highContrast;

        if (paletteChanged)
        {
            ApplyPalette();
            foreach (var window in _windows.ToArray())
            {
                ApplyWindowFrame(window);
            }
        }

        if (themeChanged)
        {
            _log.Write(AppLogLevel.Info, Category, $"Effective theme changed to {effective} (preference {Preference})");
            EffectiveThemeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // Raised on the SystemEvents thread (or ours); AppsUseLightTheme changes arrive as "General" (ImmersiveColorSet),
        // high contrast as "Accessibility"/"Color". Re-reading the registry is cheap, so re-evaluate on any category.
        _dispatcher.BeginInvoke(() =>
        {
            _systemUsesLightTheme = ReadAppsUseLightTheme();
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
            _dispatcher.BeginInvoke(Reevaluate);
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

        var palette = _highContrast
            ? BuildHighContrastPalette(_lightPalette ??= LoadPalette(LightPaletteUri))
            : _effectiveTheme == AppTheme.Dark
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

        var useDark = _effectiveTheme == AppTheme.Dark && !_highContrast ? 1 : 0;
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
        if (!_highContrast && _appliedPalette is not null)
        {
            caption = ToColorRef(_appliedPalette, "Brush.Chrome.Background") ?? DwmColorDefault;
            text = ToColorRef(_appliedPalette, "Brush.Foreground") ?? DwmColorDefault;
        }

        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_CAPTION_COLOR, in caption, sizeof(int));
        NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_TEXT_COLOR, in text, sizeof(int));
    }

    private bool ReadAppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;   // missing = light
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            _log.Write(AppLogLevel.Warning, Category, "Couldn't read AppsUseLightTheme; assuming light", ex);
            return true;
        }
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
