namespace MdReader.Core.Rendering;

/// <summary>
/// Finding a place in a rendered document from a line of its source (ARCHITECTURE M13). A rendered page carries no
/// source positions of its own — apart from task checkboxes (§16) — so the heading list a render produced is the
/// only map back, and this is the whole of it.
/// </summary>
public static class TocLookup
{
    /// <summary>
    /// The id of the last heading at or above <paramref name="line"/> (1-based), or <c>""</c> for the top of the
    /// document — which is the honest answer for a line that comes before the first heading, for a document that
    /// has none, and for a line number nobody can place.
    /// </summary>
    /// <remarks>
    /// Entries whose <see cref="TocEntry.Line"/> is 0 carry no position and are passed over. The list is in
    /// document order, so the search stops at the first heading that starts below the line.
    /// </remarks>
    public static string AnchorForLine(IReadOnlyList<TocEntry> toc, int line)
    {
        ArgumentNullException.ThrowIfNull(toc);

        var anchor = "";
        foreach (TocEntry entry in toc)
        {
            if (entry.Line <= 0)
            {
                continue;
            }

            if (entry.Line > line)
            {
                break;
            }

            anchor = entry.Id;
        }

        return anchor;
    }
}
