namespace MdReader.Web;

/// <summary>
/// The kinds of document <c>POST /api/render</c> will read, and the document name each one renders under.
/// </summary>
/// <remarks>
/// <para>The renderer works out what a document is from its path, and that same path supplies the title when the
/// document has no heading of its own. A request therefore chooses between names the server already holds, rather
/// than sending one: the client picks a kind, the server picks the path. Nothing a caller writes reaches either.</para>
/// <para>Adding a kind here is all it takes to serve another format the renderer understands — the endpoint and the
/// page both go through this.</para>
/// </remarks>
internal static class RenderKinds
{
    internal const string Markdown = "markdown";
    internal const string Json = "json";

    /// <summary>What the server calls a document of each kind. Never built from anything a request sent.</summary>
    private const string MarkdownDocument = "/document.md";
    private const string JsonDocument = "/document.json";

    /// <summary>
    /// True for a kind this server will read. Null and empty are true: a request that says nothing means Markdown,
    /// which is what every client written before JSON existed sends.
    /// </summary>
    internal static bool IsKnown(string? kind) =>
        string.IsNullOrEmpty(kind)
        || string.Equals(kind, Markdown, StringComparison.Ordinal)
        || string.Equals(kind, Json, StringComparison.Ordinal);

    /// <summary>
    /// The document name to render a kind under. Anything not recognised is Markdown, which cannot be reached
    /// through the endpoint — it refuses an unknown kind before asking — and is the safe answer for any other caller.
    /// </summary>
    internal static string DocumentNameFor(string? kind) =>
        string.Equals(kind, Json, StringComparison.Ordinal) ? JsonDocument : MarkdownDocument;
}
