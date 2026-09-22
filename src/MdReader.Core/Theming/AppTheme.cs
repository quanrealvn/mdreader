using System.Text.Json.Serialization;
using MdReader.Core.Settings;

namespace MdReader.Core.Theming;

public enum AppTheme { [JsonStringEnumMemberName("light")] Light, [JsonStringEnumMemberName("dark")] Dark }

public static class ThemeResolver
{
    public static AppTheme Resolve(ThemePreference preference, bool systemAppsUseLightTheme) => preference switch
    {
        ThemePreference.Light => AppTheme.Light,
        ThemePreference.Dark => AppTheme.Dark,
        _ => systemAppsUseLightTheme ? AppTheme.Light : AppTheme.Dark,   // System (and any undefined value) follows Windows
    };
}

public static class ThemePalette
{
    /// Page background; MUST equal CSS --mdr-bg, WebView2.DefaultBackgroundColor and the WPF DocumentHost background.
    public static (byte R, byte G, byte B) PageBackground(AppTheme theme) =>   // Light #FFFFFF, Dark #0D1117
        theme == AppTheme.Dark ? ((byte)0x0D, (byte)0x11, (byte)0x17) : ((byte)0xFF, (byte)0xFF, (byte)0xFF);
}
