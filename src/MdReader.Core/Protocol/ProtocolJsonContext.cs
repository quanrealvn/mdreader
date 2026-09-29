using System.Text.Json;
using System.Text.Json.Serialization;

namespace MdReader.Core.Protocol;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true,
    AllowOutOfOrderMetadataProperties = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(HostMessage))]
[JsonSerializable(typeof(WebMessage))]
public partial class ProtocolJsonContext : JsonSerializerContext;
