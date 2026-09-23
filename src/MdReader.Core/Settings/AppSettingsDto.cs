using System.Text.Json.Serialization;

namespace MdReader.Core.Settings;

/// Deserialization-only mirror of <see cref="AppSettings"/> with nullable properties (ARCHITECTURE §4.6).
///
/// System.Text.Json source generation deserializes an init-only-property record via an object initializer that
/// assigns every property found in the JSON; a property *absent* from settings.json is simply never assigned, so it
/// keeps whatever <c>new AppSettings()</c>'s field initializer gave it -- except that source generation actually
/// builds the object as `new AppSettings() { Prop = value, ... }` where every constructor-settable property is
/// listed, which for this record resolves to the CLR default (0 / false / first enum member) rather than the
/// initializer default for any property the JSON omitted. Deserializing into this all-nullable DTO instead lets
/// <see cref="JsonSettingsStore"/> tell "absent" (null) apart from "present" and map absent properties onto
/// <c>new AppSettings()</c>'s real defaults.
///
/// Public (not internal) because <see cref="SettingsJsonContext"/> is a public source-generated context: the
/// generator emits a public `SettingsJsonContext.Default.AppSettingsDto` accessor, and that can't return a
/// `JsonTypeInfo&lt;T&gt;` for a less-visible T (CS0053). It is not part of AppSettings' own public shape and isn't
/// meant to be used outside settings deserialization.
public sealed class AppSettingsDto
{
    public int? SchemaVersion { get; init; }
    public ThemePreference? Theme { get; init; }
    public double? Zoom { get; init; }
    public bool? TocVisible { get; init; }
    public double? TocWidth { get; init; }

    public bool? SplitView { get; init; }
    public double? SplitRatio { get; init; }

    public WindowPlacement? Window { get; init; }
    public IReadOnlyList<string>? RecentFiles { get; init; }

    [JsonConverter(typeof(SessionStateSettingConverter))]
    public SessionState? Session { get; init; }
}
