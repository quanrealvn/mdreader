using System.Globalization;

namespace MdReader.Core.Documents;

/// <summary>
/// Everything "Export as HTML" says to the user, in one testable place — the same arrangement
/// <c>DocumentErrorMessages</c> makes for loading a document.
/// </summary>
/// <remarks>
/// The success line is not just "done". An exported page can be missing things the reader could see — diagrams and
/// formulas that only exist because JavaScript drew them, a photograph too large to carry, an image that still comes
/// off the web — and the moment to say so is while the person still remembers asking. Saying nothing would ship a
/// file that quietly differs from what was on screen, which is the one outcome this feature has no excuse for.
/// </remarks>
public static class HtmlExportMessages
{
    public const string NotReady = "The document isn't ready to export yet.";

    public const string Failed = "Couldn't export the HTML.";

    public const string NoStylesheet = "The reader's stylesheet couldn't be read; the page was exported unstyled.";

    /// <summary>
    /// "Exported to guide.html — 2 diagrams and 1 formula kept as source; 1 image couldn't be included."
    /// </summary>
    public static string Exported(string fileName, HtmlExportResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // "kept as source" rather than "was/were written as": one phrase that stays grammatical whether it follows
        // one diagram or seven, which is worth more here than a livelier verb.
        var kept = new List<string>(2);
        if (result.DiagramsAsSource > 0)
        {
            kept.Add(Count(result.DiagramsAsSource, "diagram", "diagrams"));
        }

        if (result.FormulasAsSource > 0)
        {
            kept.Add(Count(result.FormulasAsSource, "formula", "formulas"));
        }

        var clauses = new List<string>(3);
        if (kept.Count > 0)
        {
            clauses.Add(string.Join(" and ", kept) + " kept as source");
        }

        if (result.ImagesDropped > 0)
        {
            clauses.Add(Count(result.ImagesDropped, "image", "images") + " couldn't be included");
        }

        if (result.ImagesLeftRemote > 0)
        {
            clauses.Add(Count(result.ImagesLeftRemote, "image", "images")
                        + (result.ImagesLeftRemote == 1 ? " still loads from the web" : " still load from the web"));
        }

        return clauses.Count == 0
            ? string.Create(CultureInfo.InvariantCulture, $"Exported to {fileName}.")
            : string.Create(CultureInfo.InvariantCulture, $"Exported to {fileName} — {string.Join("; ", clauses)}.");
    }

    private static string Count(int value, string singular, string plural) =>
        string.Create(CultureInfo.InvariantCulture, $"{value} {(value == 1 ? singular : plural)}");
}
