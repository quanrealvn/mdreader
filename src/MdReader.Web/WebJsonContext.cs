using System.Text.Json;
using System.Text.Json.Serialization;

namespace MdReader.Web;

/// <summary><c>POST /api/render</c> body: <c>{"markdown": "..."}</c>.</summary>
internal sealed record RenderRequest(string? Markdown);

/// <summary>Body of every non-200 API response: <c>{"error": "tooLarge", "message": "..."}</c>.</summary>
internal sealed record ErrorResponse(string Error, string Message);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RenderRequest))]
[JsonSerializable(typeof(ErrorResponse))]
internal sealed partial class WebJsonContext : JsonSerializerContext;
