using System.Text.RegularExpressions;
using MdReader.Core.Settings;

namespace MdReader.Core.Cli;

// Owned by WP4 (ARCHITECTURE §2.2, §4.7).
public static class CommandLineParser
{
    private const string EnvTestModeName = "MDREADER_TEST_MODE";

    // \z (not $) anchors strictly to the end of the string: '$' also matches just before a single trailing '\n',
    // which would let e.g. "abc\n" through.
    private static readonly Regex InstanceIdPattern = new(@"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string UsageText { get; } =
        """
        Usage: MdReader.exe [--instance-id <id>] [--profile-dir <dir>] [--theme system|light|dark] [--capture <out.png>] [--perf-log <out.json>] [--help|-h|/?] [--] [files...]

        Options:
          --instance-id <id>            Identify this instance (single-instance mutex/pipe name suffix); also enables test mode.
                                         Allowed characters: letters, digits, '_' and '-' (1-64 characters).
          --profile-dir <dir>           Use <dir> instead of the default AppData locations for settings, WebView2 user data and logs.
          --theme system|light|dark     Override the theme for this session only (not persisted).
          --capture <out.png>           Render headlessly and save a screenshot to <out.png>, then exit (implies standalone mode).
          --perf-log <out.json>         Write a performance report to <out.json> after the first document finishes rendering.
          --help, -h, /?                Show this help message.
          --                            Treat all further arguments as files, even if they start with '-'.
          files...                      Markdown files to open. Relative paths resolve against the current directory.
                                         'file:///' URIs are converted to local paths.
        """;

    public static CommandLineParseResult Parse(
        IReadOnlyList<string> args,
        string currentDirectory,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(currentDirectory);

        string? instanceId = null;
        string? profileDirectory = null;
        ThemePreference? themeOverride = null;
        string? capturePath = null;
        string? perfLogPath = null;
        var files = new List<string>();
        var showHelp = false;
        var optionsEnded = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];

            if (!optionsEnded && arg == "--")
            {
                optionsEnded = true;
                continue;
            }

            if (!optionsEnded && IsHelpFlag(arg))
            {
                showHelp = true;
                continue;
            }

            if (!optionsEnded && arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
            {
                var body = arg[2..];
                var eq = body.IndexOf('=');
                var name = eq >= 0 ? body[..eq] : body;
                var inlineValue = eq >= 0 ? body[(eq + 1)..] : null;
                var lowerName = name.ToLowerInvariant();

                if (lowerName == "help")
                {
                    showHelp = true;
                    continue;
                }

                if (lowerName is not ("instance-id" or "profile-dir" or "theme" or "capture" or "perf-log"))
                {
                    return Fail($"Unknown option '--{name}'.");
                }

                string value;
                if (inlineValue is not null)
                {
                    value = inlineValue;
                }
                else if (i + 1 < args.Count)
                {
                    value = args[++i];
                }
                else
                {
                    return Fail($"Missing value for option '--{name}'.");
                }

                if (value.Length == 0)
                {
                    return Fail($"Missing value for option '--{name}'.");
                }

                switch (lowerName)
                {
                    case "instance-id":
                        if (!InstanceIdPattern.IsMatch(value))
                        {
                            return Fail($"Invalid instance id '{value}'. Expected 1-64 characters of letters, digits, '_' or '-'.");
                        }

                        instanceId = value;
                        break;

                    case "profile-dir":
                        profileDirectory = ResolveFullPath(value, currentDirectory);
                        break;

                    case "theme":
                        if (!TryParseTheme(value, out var theme))
                        {
                            return Fail($"Invalid theme '{value}'. Expected 'system', 'light', or 'dark'.");
                        }

                        themeOverride = theme;
                        break;

                    case "capture":
                        capturePath = ResolveFullPath(value, currentDirectory);
                        break;

                    case "perf-log":
                        perfLogPath = ResolveFullPath(value, currentDirectory);
                        break;
                }

                continue;
            }

            if (!optionsEnded && arg.Length > 1 && arg[0] == '-')
            {
                return Fail($"Unknown option '{arg}'.");
            }

            if (arg.Length == 0)
            {
                return Fail("Empty file path.");
            }

            files.Add(ResolveFilePath(arg, currentDirectory));
        }

        var isTestMode = instanceId is not null || getEnvironmentVariable?.Invoke(EnvTestModeName) == "1";

        var options = new CommandLineOptions
        {
            InstanceId = instanceId ?? CommandLineOptions.DefaultInstanceId,
            ProfileDirectory = profileDirectory,
            ThemeOverride = themeOverride,
            CapturePath = capturePath,
            PerfLogPath = perfLogPath,
            Files = files,
            ShowHelp = showHelp,
            IsTestMode = isTestMode,
        };

        return new CommandLineParseResult(options, null);
    }

    private static bool IsHelpFlag(string arg) =>
        string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase) ||
        arg == "/?";

    private static bool TryParseTheme(string value, out ThemePreference theme)
    {
        switch (value.ToLowerInvariant())
        {
            case "system":
                theme = ThemePreference.System;
                return true;
            case "light":
                theme = ThemePreference.Light;
                return true;
            case "dark":
                theme = ThemePreference.Dark;
                return true;
            default:
                theme = default;
                return false;
        }
    }

    private static string ResolveFilePath(string arg, string currentDirectory)
    {
        if (TryConvertFileUri(arg, out var localPath))
        {
            arg = localPath;
        }

        return ResolveFullPath(arg, currentDirectory);
    }

    private static bool TryConvertFileUri(string arg, out string localPath)
    {
        if (arg.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(arg, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            localPath = uri.LocalPath;
            return true;
        }

        localPath = arg;
        return false;
    }

    private static string ResolveFullPath(string path, string currentDirectory) => Path.GetFullPath(path, currentDirectory);

    private static CommandLineParseResult Fail(string message) => new(null, message);
}
