namespace MdReader.Core.Folders;

/// <summary>One file the quick-open box can offer. Purely lexical: nothing here touches the file system.</summary>
/// <param name="RelativePath">The path as the user sees it under the open folder, or the full path when there is none.</param>
public sealed record QuickOpenCandidate(string FullPath, string FileName, string RelativePath);

public sealed record QuickOpenResult(QuickOpenCandidate Candidate, int Score);

/// <summary>
/// Ranks the quick-open candidates for a typed query (ARCHITECTURE M13). A match in the file name always beats a
/// match that only exists somewhere in the path, so typing "readme" offers <c>README.md</c> before
/// <c>readme-drafts/notes.md</c>.
/// </summary>
public static class QuickOpenRanker
{
    public const int DefaultLimit = 50;

    /// <summary>
    /// How many matching candidates are scored in full. Finding the matches is linear and stays cheap on a folder
    /// of thousands of files; scoring them is not, so a one-letter query that matches everything scores only the
    /// first <see cref="MaxScoredCandidates"/> of them (in the caller's order) and leaves the rest out. Typing a
    /// second letter is what narrows the list anyway, and this is what keeps every keystroke off the stall budget.
    /// </summary>
    public const int MaxScoredCandidates = 2_000;

    /// <summary>Added to a file-name match so it outranks every path-only match, whatever the path scores.</summary>
    internal const int FileNameBonus = 1_000;

    /// <summary>
    /// The best <paramref name="limit"/> candidates for <paramref name="query"/>, best first. An empty query keeps
    /// the given order (the caller supplies the order it wants: folder order, or most recent first).
    /// </summary>
    public static IReadOnlyList<QuickOpenResult> Rank(IReadOnlyList<QuickOpenCandidate> candidates, string? query,
                                                      int limit = DefaultLimit)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (limit <= 0 || candidates.Count == 0)
        {
            return [];
        }

        ReadOnlySpan<char> trimmed = (query ?? "").AsSpan().Trim();
        if (trimmed.Length == 0)
        {
            var head = new List<QuickOpenResult>(Math.Min(limit, candidates.Count));
            for (int i = 0; i < candidates.Count && head.Count < limit; i++)
            {
                head.Add(new QuickOpenResult(candidates[i], 0));
            }

            return head;
        }

        var matches = new List<QuickOpenResult>();
        foreach (QuickOpenCandidate candidate in candidates)
        {
            // A file name is a suffix of its relative path, so anything matching the name matches the path too:
            // one linear test decides whether this candidate is worth scoring at all.
            if (!FuzzyMatcher.IsSubsequence(candidate.RelativePath, trimmed))
            {
                continue;
            }

            if (matches.Count >= MaxScoredCandidates)
            {
                break;
            }

            if (Score(candidate, trimmed) is { } score)
            {
                matches.Add(new QuickOpenResult(candidate, score));
            }
        }

        matches.Sort(CompareResults);
        if (matches.Count > limit)
        {
            matches.RemoveRange(limit, matches.Count - limit);
        }

        return matches;
    }

    private static int? Score(QuickOpenCandidate candidate, ReadOnlySpan<char> query)
    {
        if (FuzzyMatcher.Score(candidate.FileName, query) is { } nameScore)
        {
            return nameScore + FileNameBonus;
        }

        return FuzzyMatcher.Score(candidate.RelativePath, query);
    }

    /// <summary>Best score first; shorter paths win ties, and the path itself breaks the rest, so the order is stable.</summary>
    private static int CompareResults(QuickOpenResult left, QuickOpenResult right)
    {
        int byScore = right.Score.CompareTo(left.Score);
        if (byScore != 0)
        {
            return byScore;
        }

        int byLength = left.Candidate.RelativePath.Length.CompareTo(right.Candidate.RelativePath.Length);
        return byLength != 0 ? byLength : string.CompareOrdinal(left.Candidate.RelativePath, right.Candidate.RelativePath);
    }
}
