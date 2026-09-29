using MdReader.Core.Paths;

namespace MdReader.Core.Hosting;

/// <summary>
/// Where this platform expects an application to keep its settings, its cache and its logs. One implementation per
/// convention: the Windows known folders, Apple's <c>~/Library</c> layout, and the XDG base directories on Linux.
/// </summary>
/// <remarks>
/// Every implementation takes the directories it builds on as constructor arguments rather than reading the
/// environment itself, so all three can be exercised from one test run on any host. <see cref="Current"/> is the one
/// wired to the real environment.
/// </remarks>
public abstract class AppDirectories
{
    /// <summary>The folder name MdReader uses inside each of the platform's directories.</summary>
    public const string ApplicationFolderName = "MdReader";

    protected AppDirectories(PathPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        Policy = policy;
    }

    /// <summary>The directories for the running operating system.</summary>
    public static AppDirectories Current { get; } = Detect();

    /// <summary>Holds the user's settings; <c>settings.json</c> lives here.</summary>
    public abstract string SettingsDirectory { get; }

    /// <summary>Holds the embedded browser's profile and anything else that may be deleted without losing data.</summary>
    public abstract string CacheDirectory { get; }

    /// <summary>Holds the rolling log files.</summary>
    public abstract string LogsDirectory { get; }

    /// <summary>The path rules these directories are spelled with, so callers build on top of the same ones.</summary>
    public PathPolicy Policy { get; }

    private static AppDirectories Detect()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsAppDirectories(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()
            ? new MacOsAppDirectories(home)
            : new XdgAppDirectories(home, Environment.GetEnvironmentVariable);
    }
}

/// <summary>
/// The Windows layout, unchanged since v1: roaming settings, everything else under the local profile. Existing
/// installations must keep finding their <c>settings.json</c>, so the two known folders are read through
/// <c>Environment.GetFolderPath</c> rather than <c>%APPDATA%</c>, which a redirected profile can disagree with.
/// </summary>
public sealed class WindowsAppDirectories : AppDirectories
{
    private readonly string _roamingAppData;
    private readonly string _localAppData;

    public WindowsAppDirectories(string roamingAppData, string localAppData, PathPolicy? policy = null)
        : base(policy ?? PathPolicy.Windows)
    {
        ArgumentNullException.ThrowIfNull(roamingAppData);
        ArgumentNullException.ThrowIfNull(localAppData);
        _roamingAppData = roamingAppData;
        _localAppData = localAppData;
    }

    public override string SettingsDirectory => Policy.Join(_roamingAppData, ApplicationFolderName);

    public override string CacheDirectory => Policy.Join(Policy.Join(_localAppData, ApplicationFolderName), "WebView2");

    public override string LogsDirectory => Policy.Join(Policy.Join(_localAppData, ApplicationFolderName), "logs");
}

/// <summary>
/// Apple's layout: <c>~/Library/Application Support</c>, <c>~/Library/Caches</c> and <c>~/Library/Logs</c>, each with
/// the application's own folder inside. Logs go in their own tree rather than under the application-support folder so
/// Console.app finds them where it expects.
/// </summary>
public sealed class MacOsAppDirectories : AppDirectories
{
    private readonly string _home;

    public MacOsAppDirectories(string homeDirectory, PathPolicy? policy = null)
        : base(policy ?? PathPolicy.MacOs)
    {
        ArgumentException.ThrowIfNullOrEmpty(homeDirectory);
        _home = homeDirectory;
    }

    public override string SettingsDirectory => Library("Application Support");

    public override string CacheDirectory => Library("Caches");

    public override string LogsDirectory => Library("Logs");

    private string Library(string folder) =>
        Policy.Join(Policy.Join(Policy.Join(_home, "Library"), folder), ApplicationFolderName);
}

/// <summary>
/// The XDG base directory specification used on Linux: <c>$XDG_CONFIG_HOME</c>, <c>$XDG_CACHE_HOME</c> and
/// <c>$XDG_STATE_HOME</c>, falling back to <c>~/.config</c>, <c>~/.cache</c> and <c>~/.local/state</c>. A variable that
/// is empty or not an absolute path is ignored, as the specification requires.
/// </summary>
public sealed class XdgAppDirectories : AppDirectories
{
    private readonly string _home;
    private readonly Func<string, string?> _environmentVariables;

    public XdgAppDirectories(string homeDirectory, Func<string, string?> environmentVariables, PathPolicy? policy = null)
        : base(policy ?? PathPolicy.Posix)
    {
        ArgumentException.ThrowIfNullOrEmpty(homeDirectory);
        ArgumentNullException.ThrowIfNull(environmentVariables);
        _home = homeDirectory;
        _environmentVariables = environmentVariables;
    }

    public override string SettingsDirectory => Base("XDG_CONFIG_HOME", ".config");

    public override string CacheDirectory => Base("XDG_CACHE_HOME", ".cache");

    public override string LogsDirectory => Policy.Join(Base("XDG_STATE_HOME", ".local/state"), "logs");

    private string Base(string variable, string fallback)
    {
        var configured = _environmentVariables(variable);
        var directory = Policy.IsFullyQualified(configured) ? configured! : Policy.Join(_home, fallback);
        return Policy.Join(directory, ApplicationFolderName);
    }
}
