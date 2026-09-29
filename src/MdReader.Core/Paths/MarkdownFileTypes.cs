using System.Collections.ObjectModel;

namespace MdReader.Core.Paths;

/// <summary>File-type rules for Markdown documents (ARCHITECTURE §4.3). Purely lexical.</summary>
public static class MarkdownFileTypes
{
    private static readonly ReadOnlyCollection<string> ExtensionList =
        Array.AsReadOnly([".md", ".markdown", ".mdown", ".mkd", ".mkdn"]);

    private static readonly ReadOnlyCollection<string> DirectoryIndexNameList =
        Array.AsReadOnly(["README.md", "README.markdown"]);

    private static readonly string FilterPatterns = string.Join(';', ExtensionList.Select(extension => "*" + extension));

    /// <summary>.md .markdown .mdown .mkd .mkdn (lower case, with the leading dot).</summary>
    public static IReadOnlyList<string> Extensions => ExtensionList;

    /// <summary>File names that stand for a folder when a link points at the folder, in priority order.</summary>
    public static IReadOnlyList<string> DirectoryIndexNames => DirectoryIndexNameList;

    /// <summary>"Markdown files (*.md;…)|*.md;…|All files (*.*)|*.*" for Win32 file dialogs.</summary>
    public static string OpenFileDialogFilter { get; } = $"Markdown files ({FilterPatterns})|{FilterPatterns}|All files (*.*)|*.*";

    /// <summary>True if the path's extension is a Markdown extension (OrdinalIgnoreCase). Never touches the file system.</summary>
    public static bool IsMarkdownPath(string path)
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
