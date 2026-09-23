using System.Text.Json;
using System.Text.Json.Serialization;

namespace MdReader.Core.Settings;

public enum ThemePreference
{
    [JsonStringEnumMemberName("system")] System,
    [JsonStringEnumMemberName("light")] Light,
    [JsonStringEnumMemberName("dark")] Dark,
}

public sealed record WindowPlacement(double Left, double Top, double Width, double Height, bool IsMaximized);

/// The tabs to reopen at the next start (ARCHITECTURE §4.6): fully-qualified paths in tab order, and the active one
/// (null, or one of <see cref="Files"/>).
public sealed record SessionState(IReadOnlyList<string> Files, string? ActiveFile)
{
    public const int MaxFiles = 50;
}

public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public ThemePreference Theme { get; init; } = ThemePreference.System;
    public double Zoom { get; init; } = 1.0;                       // ZoomLevels.Min..Max
    public bool TocVisible { get; init; } = true;

    /// Side-by-side editing: the view mode a new tab starts in (false = preview only).
    public bool SplitView { get; init; }

    /// Fraction of the document area the editor pane takes in split view; clamped to MinSplitRatio..MaxSplitRatio.
    public double SplitRatio { get; init; } = DefaultSplitRatio;

    public const double DefaultSplitRatio = 0.5;
    public const double MinSplitRatio = 0.15;
    public const double MaxSplitRatio = 0.85;

    public WindowPlacement? Window { get; init; }
    public IReadOnlyList<string> RecentFiles { get; init; } = [];   // most recent first, max 10, case-insensitive unique

    /// The last session's tabs; null = none recorded yet (then "session" is left out of settings.json).
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SessionState? Session { get; init; }
}

/// settings.json only: a "session" of an unexpected shape (a newer version's format, a hand edit) reads as null or loses
/// only its bad parts (non-string file entries, a non-string "activeFile") instead of making the whole file "corrupt" and
/// resetting every other setting. Paths are checked by AppSettingsNormalizer.
internal sealed class SessionStateSettingConverter : JsonConverter<SessionState>
{
    public override bool HandleNull => true;

    public override SessionState? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();   // arrays: consume the whole value; primitives and null: no-op
            return null;
        }

        var files = new List<string>();
        string? activeFile = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            reader.Read();
            if (string.Equals(name, "files", StringComparison.OrdinalIgnoreCase) && reader.TokenType == JsonTokenType.StartArray)
            {
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.String)
                    {
                        files.Add(reader.GetString()!);
                    }
                    else
                    {
                        reader.Skip();
                    }
                }
            }
            else if (string.Equals(name, "activeFile", StringComparison.OrdinalIgnoreCase) && reader.TokenType == JsonTokenType.String)
            {
                activeFile = reader.GetString();
            }
            else
            {
                reader.Skip();
            }
        }

        return new SessionState(files, activeFile);
    }

    public override void Write(Utf8JsonWriter writer, SessionState value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteStartArray("files");
        foreach (var file in value.Files)
        {
            writer.WriteStringValue(file);
        }

        writer.WriteEndArray();
        writer.WriteString("activeFile", value.ActiveFile);
        writer.WriteEndObject();
    }
}
