using MdReader.Core.Settings;

namespace MdReader.Core.Cli;

public sealed record CommandLineOptions
{
    public const string DefaultInstanceId = "default";
    public string InstanceId { get; init; } = DefaultInstanceId;   // ^[A-Za-z0-9_-]{1,64}$
    public string? ProfileDirectory { get; init; }                 // full path; created if missing
    public ThemePreference? ThemeOverride { get; init; }           // session only, not persisted
    public string? CapturePath { get; init; }                      // full path of .png
    public string? PerfLogPath { get; init; }                      // full path of .json
    public IReadOnlyList<string> Files { get; init; } = [];        // full paths (resolved against cwd)
    public bool ShowHelp { get; init; }
    public bool IsTestMode { get; init; }                          // --instance-id given OR env MDREADER_TEST_MODE=1
}

public sealed record CommandLineParseResult(CommandLineOptions? Options, string? Error)
{
    public bool IsSuccess => Options is not null;
}
