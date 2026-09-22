namespace MdReader.Core.Hosting;

// Owned by WP4 (ARCHITECTURE §2.2, §4.9).
public sealed record AppPaths(string SettingsFile, string WebView2UserDataFolder, string LogsFolder, string WebRoot)
{
    /// profileDirectory null → %APPDATA%\MdReader\settings.json, %LOCALAPPDATA%\MdReader\WebView2, %LOCALAPPDATA%\MdReader\logs.
    /// else <profile>\settings.json, <profile>\WebView2, <profile>\logs. WebRoot = <appBaseDirectory>\web.
    public static AppPaths Create(string? profileDirectory, string appBaseDirectory)
    {
        ArgumentNullException.ThrowIfNull(appBaseDirectory);

        string settingsFile;
        string webView2UserDataFolder;
        string logsFolder;

        if (string.IsNullOrEmpty(profileDirectory))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            settingsFile = Path.Combine(appData, "MdReader", "settings.json");
            webView2UserDataFolder = Path.Combine(localAppData, "MdReader", "WebView2");
            logsFolder = Path.Combine(localAppData, "MdReader", "logs");
        }
        else
        {
            settingsFile = Path.Combine(profileDirectory, "settings.json");
            webView2UserDataFolder = Path.Combine(profileDirectory, "WebView2");
            logsFolder = Path.Combine(profileDirectory, "logs");
        }

        var webRoot = Path.Combine(appBaseDirectory, "web");

        return new AppPaths(settingsFile, webView2UserDataFolder, logsFolder, webRoot);
    }
}
