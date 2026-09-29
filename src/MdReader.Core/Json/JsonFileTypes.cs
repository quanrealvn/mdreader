using System.Collections.ObjectModel;

namespace MdReader.Core.Json;

/// <summary>
/// File-type rules for JSON documents (JSON-PLAN, "Detection"). Purely lexical: nothing here touches the file system.
/// </summary>
/// <remarks>
/// <c>.map</c> and <c>.lock</c> are JSON too and are deliberately left out — nobody opens them to read. <c>.jsonl</c>
/// and <c>.ndjson</c> are listed because the extension is how the reader is offered at all; phase 1 renders a
/// line-delimited file as a parse error that says what the file looks like, which is more use than refusing to open it.
/// </remarks>
public static class JsonFileTypes
{
    private static readonly ReadOnlyCollection<string> ExtensionList =
        Array.AsReadOnly([".json", ".jsonl", ".ndjson", ".geojson"]);

    private static readonly string FilterPatterns = string.Join(';', ExtensionList.Select(extension => "*" + extension));

    /// <summary>.json .jsonl .ndjson .geojson (lower case, with the leading dot).</summary>
    public static IReadOnlyList<string> Extensions => ExtensionList;

    /// <summary>"JSON files (*.json;…)|*.json;…" for a Win32 file dialog's filter list.</summary>
    public static string OpenFileDialogFilter { get; } = $"JSON files ({FilterPatterns})|{FilterPatterns}";

    /// <summary>True if the path's extension is a JSON extension (OrdinalIgnoreCase). Never touches the file system.</summary>
    public static bool IsJsonPath(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path.AsSpan());
        foreach (var candidate in ExtensionList)
        {
            if (extension.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
