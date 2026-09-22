namespace MdReader.Core.Rendering;

/// <summary>
/// The single rule for content ids (JS mirrors it, see ARCHITECTURE §7.3). Used for heading ids in the TOC and for every
/// <c>id</c> attribute that leaves the sanitizer, so the two always agree.
/// </summary>
public static class ContentIdPolicy
{
    public const string ReservedPrefix = "mdr-";
    public const string UserContentPrefix = "user-content-";

    /// null/whitespace → null (drop). Starts with "mdr-" (OrdinalIgnoreCase) → "user-content-" + id. Otherwise unchanged.
    public static string? ToContentId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return id.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase) ? UserContentPrefix + id : id;
    }
}
