using Avalonia;
using Avalonia.Platform;
using MdReader.Shell.Services;

namespace MdReader.Ui.Services;

/// <summary>
/// The OS light/dark and contrast settings as Avalonia reports them (<see cref="IPlatformSettings"/>). On Windows that
/// is the same Personalize setting the WPF shell reads from the registry, delivered through Avalonia's own
/// WM_SETTINGCHANGE handling; on macOS it will be <c>NSApp.effectiveAppearance</c>, which is why the Avalonia shell
/// asks the framework rather than the registry.
/// </summary>
public sealed class AvaloniaThemeProbe : ISystemThemeProbe, IDisposable
{
    private readonly IPlatformSettings? _settings;
    private bool _disposed;

    public AvaloniaThemeProbe()
    {
        _settings = Application.Current?.PlatformSettings;
        if (_settings is not null)
        {
            _settings.ColorValuesChanged += OnColorValuesChanged;
        }
    }

    /// False only when the OS is explicitly in dark mode; an unknown value means light.
    public bool AppsUseLightTheme => Current()?.ThemeVariant != PlatformThemeVariant.Dark;

    public bool IsHighContrast => Current()?.ContrastPreference == ColorContrastPreference.High;

    public event EventHandler? Changed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_settings is not null)
        {
            _settings.ColorValuesChanged -= OnColorValuesChanged;
        }
    }

    private PlatformColorValues? Current() => _settings?.GetColorValues();

    private void OnColorValuesChanged(object? sender, PlatformColorValues values) => Changed?.Invoke(this, EventArgs.Empty);
}
