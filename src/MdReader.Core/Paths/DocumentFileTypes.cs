using System.Collections.ObjectModel;
using MdReader.Core.Json;

namespace MdReader.Core.Paths;

/// <summary>
/// Everything MdReader opens as a document (ARCHITECTURE §4.3): the Markdown extensions and the JSON ones, together.
/// Purely lexical, like the two lists it is made of — nothing here touches the file system.
/// </summary>
/// <remarks>
/// <para>Most of the places that used to ask <see cref="MarkdownFileTypes.IsMarkdownPath"/> were not really asking
/// about Markdown. They were asking "is this a file this application reads?", and for as long as Markdown was the
/// only answer the two questions looked like one. They are not: a <c>.json</c> file renders through
/// <c>DocumentRenderer</c> exactly as a <c>.md</c> file does, so a gate that still asks the narrower question turns
/// a document the app can render into one the user cannot reach. Those gates ask this type instead.</para>
/// <para>The gates that genuinely still mean Markdown keep asking <see cref="MarkdownFileTypes"/>: a wiki link's
/// implied extension, and the <c>README.md</c> that stands in for a folder, are Markdown conventions and have no
/// JSON counterpart.</para>
/// <para>Neither list is repeated here. Both are read from the type that owns them, so an extension added to either
/// one appears in the combined list, in the dialog filter and in every gate in the same edit.</para>
/// </remarks>
public static class DocumentFileTypes
{
    private static readonly ReadOnlyCollection<string> ExtensionList =
        Array.AsReadOnly([.. MarkdownFileTypes.Extensions, .. JsonFileTypes.Extensions]);

    /// <summary>The Markdown extensions followed by the JSON ones (lower case, with the leading dot).</summary>
    public static IReadOnlyList<string> Extensions => ExtensionList;

    /// <summary>
    /// The Open dialog's filter list: everything MdReader reads first (so it is what the dialog starts on), then
    /// Markdown alone and JSON alone for someone hunting one kind in a crowded folder, then "All files" — which is
    /// there because the command line has always opened a file of any name and the dialog should not be stricter.
    /// </summary>
    public static string OpenFileDialogFilter { get; } = BuildOpenFileDialogFilter();

    /// <summary>
    /// True if the path's extension is one MdReader renders, Markdown or JSON (OrdinalIgnoreCase). Never touches the
    /// file system.
    /// </summary>
    public static bool IsDocumentPath(string path) =>
        MarkdownFileTypes.IsMarkdownPath(path) || JsonFileTypes.IsJsonPath(path);

    private static string BuildOpenFileDialogFilter()
    {
        string markdown = PatternsFor(MarkdownFileTypes.Extensions);
        string json = PatternsFor(JsonFileTypes.Extensions);
        string both = $"{markdown};{json}";
        return $"Markdown and JSON files ({both})|{both}"
             + $"|Markdown files ({markdown})|{markdown}"
             + $"|JSON files ({json})|{json}"
             + "|All files (*.*)|*.*";
    }

    private static string PatternsFor(IReadOnlyList<string> extensions) =>
        string.Join(';', extensions.Select(extension => "*" + extension));
}
