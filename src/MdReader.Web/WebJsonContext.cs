using System.Text.Json;
using System.Text.Json.Serialization;

namespace MdReader.Web;

/// <summary>
/// <c>POST /api/render</c> body: <c>{"markdown": "...", "kind": "markdown" | "json"}</c>.
/// </summary>
/// <remarks>
/// <para><c>kind</c> is optional and defaults to Markdown, so every client written before JSON existed keeps
/// working unchanged.</para>
/// <para>It is a kind rather than a file name on purpose. The renderer decides what a document is from its path,
/// and that path also supplies the title — so letting the client send one would put its text into both. A closed
/// set of two words means the server picks the path itself and the request can only choose between them.</para>
/// </remarks>
internal sealed record RenderRequest(string? Markdown, string? Kind);

/// <summary>Body of every non-200 API response: <c>{"error": "tooLarge", "message": "..."}</c>.</summary>
internal sealed record ErrorResponse(string Error, string Message);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RenderRequest))]
[JsonSerializable(typeof(ErrorResponse))]
// The service worker's precache list, written into sw.js as a JSON array.
[JsonSerializable(typeof(List<string>))]
internal sealed partial class WebJsonContext : JsonSerializerContext;
