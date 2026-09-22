namespace MdReader.Core.Settings;

// Owned by WP4 (ARCHITECTURE §2.2, §4.6).
public static class RecentFiles
{
    public const int MaxCount = 10;

    /// Moves fullPath to the front (removing any case-insensitive duplicate), then truncates to MaxCount.
    public static IReadOnlyList<string> Add(IReadOnlyList<string> current, string fullPath)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(fullPath);

        var result = new List<string>(Math.Min(current.Count + 1, MaxCount)) { fullPath };

        foreach (var path in current)
        {
            if (result.Count >= MaxCount)
            {
                break;
            }

            if (!string.Equals(path, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(path);
            }
        }

        return result;
    }

    public static IReadOnlyList<string> Remove(IReadOnlyList<string> current, string fullPath)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(fullPath);

        return current.Where(path => !string.Equals(path, fullPath, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
