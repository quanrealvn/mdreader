using System.Text.Json;
using System.Text.Json.Serialization;

namespace MdReader.Core.Settings;

public enum ThemePreference
{
    [JsonStringEnumMemberName("system")] System,
    [JsonStringEnumMemberName("light")] Light,
    [JsonStringEnumMemberName("dark")] Dark,
}

/// How the rendered document colors its structure (ARCHITECTURE §14).
public enum ReadingStyle
{
    [JsonStringEnumMemberName("colorful")] Colorful,
    [JsonStringEnumMemberName("classic")] Classic,
}

public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool IsMaximized);

public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public ThemePreference Theme { get; init; } = ThemePreference.System;
    public double Zoom { get; init; } = 1.0;                       // ZoomLevels.Min..Max
    public bool TocVisible { get; init; } = true;

    [JsonConverter(typeof(ReadingStyleSettingConverter))]
    public ReadingStyle ReadingStyle { get; init; } = ReadingStyle.Colorful;   // unknown value in settings.json → Colorful

    public WindowPlacement? Window { get; init; }
    public IReadOnlyList<string> RecentFiles { get; init; } = [];   // most recent first, max 10, case-insensitive unique
}

/// settings.json only: an unknown "readingStyle" (a newer version's style, a typo, a number, null) reads as Colorful
/// instead of making the whole file "corrupt" and resetting every other setting (ARCHITECTURE §14).
internal sealed class ReadingStyleSettingConverter : JsonConverter<ReadingStyle>
{
    public override bool HandleNull => true;

    public override ReadingStyle Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && string.Equals(reader.GetString(), "classic", StringComparison.OrdinalIgnoreCase))
        {
            return ReadingStyle.Classic;
        }

        reader.Skip();   // objects/arrays: consume the whole value; primitives: no-op
        return ReadingStyle.Colorful;
    }

    public override void Write(Utf8JsonWriter writer, ReadingStyle value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value == ReadingStyle.Classic ? "classic" : "colorful");
}
