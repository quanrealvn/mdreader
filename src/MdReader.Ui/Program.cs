using Avalonia;
using MdReader.Ui.Spike;
using MdReader.Ui.WebView;
using MdReader.Core.Cli;
using MdReader.Core.Diagnostics;
using MdReader.Core.Hosting;

namespace MdReader.Ui;

internal static class Program
{
    private const string Category = "Startup";
    private const string DefaultSample = "docs/samples/showcase.md";

    /// <summary>
    /// STA is not optional: WebView2 is COM, and <c>CreateCoreWebView2ControllerAsync</c> completes through the same
    /// Win32 message loop Avalonia pumps.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        CommandLineParseResult parsed = CommandLineParser.Parse(args, Environment.CurrentDirectory);
        if (!parsed.IsSuccess)
        {
            Console.Error.WriteLine(parsed.Error);
            Console.Error.WriteLine(CommandLineParser.UsageText);
            return SpikeExitCodes.CommandLineError;
        }

        CommandLineOptions options = parsed.Options!;
        if (options.ShowHelp)
        {
            Console.Error.WriteLine(CommandLineParser.UsageText);
            return SpikeExitCodes.Success;
        }

        string? documentPath = options.Files.Count > 0 ? options.Files[0] : FindDefaultSample();
        if (documentPath is null)
        {
            Console.Error.WriteLine($"No file given and the default sample ({DefaultSample}) isn't next to the build output.");
            return SpikeExitCodes.CommandLineError;
        }

        AppPaths paths = AppPaths.Create(options.ProfileDirectory ?? DefaultProfileDirectory(), AppContext.BaseDirectory);
        Directory.CreateDirectory(paths.LogsFolder);
#if DEBUG
        const AppLogLevel MinimumLevel = AppLogLevel.Debug;
#else
        const AppLogLevel MinimumLevel = AppLogLevel.Info;
#endif
        using var fileLog = new FileAppLog(paths.LogsFolder, MinimumLevel);
        var log = new CompositeAppLog(fileLog, new StandardErrorAppLog(MinimumLevel));
        log.Write(AppLogLevel.Info, Category,
            $"MdReader Avalonia spike starting: {documentPath}; profile {paths.LogsFolder}; capture {options.CapturePath ?? "(none)"}.");

        var environment = new WebViewEnvironmentFactory(paths.WebView2UserDataFolder, log);
        var context = new SpikeContext(options, paths, log, documentPath, environment);

        try
        {
            return AppBuilder.Configure(() => new App(context))
                .UsePlatformDetect()
                .LogToTrace()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            log.Write(AppLogLevel.Error, Category, "The application failed.", ex);
            Console.Error.WriteLine(ex);
            return SpikeExitCodes.CaptureFailed;
        }
    }

    /// <summary>
    /// The spike keeps its WebView2 user data, logs and settings out of the real app's profile so it can never disturb
    /// a running MdReader (the environment options would have to match exactly if they shared a folder).
    /// </summary>
    private static string DefaultProfileDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdReader", "AvaloniaSpike");

    /// <summary>Walks up from the build output looking for the repo's sample, so plain <c>dotnet run</c> shows something.</summary>
    private static string? FindDefaultSample()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, DefaultSample.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }
}
