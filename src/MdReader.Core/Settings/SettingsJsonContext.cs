using System.Text.Json;
using System.Text.Json.Serialization;

namespace MdReader.Core.Settings;

// camelCase property names, indented output, enum members honor [JsonStringEnumMemberName] (ARCHITECTURE §4.6).
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(AppSettingsDto))]
public partial class SettingsJsonContext : JsonSerializerContext;
