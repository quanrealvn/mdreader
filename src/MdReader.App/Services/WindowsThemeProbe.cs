using System.Windows;
using MdReader.Core.Diagnostics;
using MdReader.Shell.Services;
using Microsoft.Win32;

namespace MdReader.App.Services;

/// <summary>
/// Windows' light/dark and high-contrast settings for <see cref="ThemeService"/>: the Personalize registry value plus
/// <see cref="SystemParameters.HighContrast"/>, refreshed on <see cref="SystemEvents.UserPreferenceChanged"/>.
/// </summary>
public sealed class WindowsThemeProbe : ISystemThemeProbe, IDisposable
{
    private const string Category = "Theme";
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly IAppLog _log;
    private bool _disposed;

    public WindowsThemeProbe(IAppLog log)
    {
        _log = log;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public bool AppsUseLightTheme
    {
        get
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
    }

    public bool IsHighContrast => SystemParameters.HighContrast;

    public event EventHandler? Changed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    // AppsUseLightTheme changes arrive as "General" (ImmersiveColorSet), high contrast as "Accessibility"/"Color".
    // Re-reading both values is cheap, so every category is reported.
    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);
}
