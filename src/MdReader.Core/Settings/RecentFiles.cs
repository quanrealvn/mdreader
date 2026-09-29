using MdReader.Core.Paths;

namespace MdReader.Core.Settings;

// Owned by WP4 (ARCHITECTURE §2.2, §4.6).
public static class RecentFiles
{
    public const int MaxCount = 10;

    /// Moves fullPath to the front (removing any duplicate, matched the way this platform matches file names:
    /// ignoring case on Windows and macOS, exactly on Linux), then truncates to MaxCount.
    public static IReadOnlyList<string> Add(IReadOnlyList<string> current, string fullPath)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(fullPath);

        var comparison = PathPolicy.Current.Comparison;
        var result = new List<string>(Math.Min(current.Count + 1, MaxCount)) { fullPath };

        foreach (var path in current)
        {
            if (result.Count >= MaxCount)
            {
                break;
            }

            if (!string.Equals(path, fullPath, comparison))
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

        var comparison = PathPolicy.Current.Comparison;
        return current.Where(path => !string.Equals(path, fullPath, comparison)).ToList();
    }
}
