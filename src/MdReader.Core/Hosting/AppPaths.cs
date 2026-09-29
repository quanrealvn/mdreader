using MdReader.Core.Paths;

namespace MdReader.Core.Hosting;

// Owned by WP4 (ARCHITECTURE §2.2, §4.9).
public sealed record AppPaths(string SettingsFile, string CacheFolder, string LogsFolder, string WebRoot)
{
    /// <summary>The embedded browser's profile folder, which is the app's cache folder: WebView2's user data folder on
    /// Windows, the WKWebsiteDataStore directory on macOS.</summary>
    public string WebView2UserDataFolder => CacheFolder;

    /// profileDirectory null → the platform's own directories (<see cref="AppDirectories"/>): on Windows unchanged
    /// from v1, i.e. %APPDATA%\MdReader\settings.json, %LOCALAPPDATA%\MdReader\WebView2 and \logs; on macOS
    /// ~/Library/Application Support|Caches|Logs/MdReader; on Linux the XDG directories.
    /// else &lt;profile&gt;\settings.json, &lt;profile&gt;\WebView2, &lt;profile&gt;\logs.
    /// WebRoot = &lt;appBaseDirectory&gt;\web.
    public static AppPaths Create(string? profileDirectory, string appBaseDirectory, AppDirectories? directories = null)
    {
        ArgumentNullException.ThrowIfNull(appBaseDirectory);
        directories ??= AppDirectories.Current;
        var policy = directories.Policy;

        string settingsFile;
        string cacheFolder;
        string logsFolder;

        if (string.IsNullOrEmpty(profileDirectory))
        {
            settingsFile = policy.Join(directories.SettingsDirectory, "settings.json");
            cacheFolder = directories.CacheDirectory;
            logsFolder = directories.LogsDirectory;
        }
        else
        {
            settingsFile = policy.Join(profileDirectory, "settings.json");
            cacheFolder = policy.Join(profileDirectory, "WebView2");
            logsFolder = policy.Join(profileDirectory, "logs");
        }

        return new AppPaths(settingsFile, cacheFolder, logsFolder, policy.Join(appBaseDirectory, "web"));
    }
}
