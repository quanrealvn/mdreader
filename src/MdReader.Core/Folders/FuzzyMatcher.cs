namespace MdReader.Core.Folders;

/// <summary>
/// Scores how well a typed query matches a candidate string, the way a quick-open box is expected to rank
/// (ARCHITECTURE M13). The query must appear as a subsequence; everything else is ranking, not filtering.
/// </summary>
/// <remarks>
/// <para>Matching is ordinal and case-insensitive. A match is worth more when its characters run together, when they
/// start a word or a path segment, and when they sit near the front; characters skipped in between cost a little.</para>
/// <para>The score is computed with a small dynamic program over two rolling rows, so a candidate costs
/// O(text × query) time and no allocation. Both sides are capped (<see cref="MaxTextChars"/>,
/// <see cref="MaxQueryChars"/>) so one pathological path can't make a keystroke slow.</para>
/// </remarks>
public static class FuzzyMatcher
{
    /// <summary>Characters of a candidate that take part in scoring; the rest can still be matched, just not scored.</summary>
    public const int MaxTextChars = 512;

    /// <summary>Longer queries fall back to a plain subsequence test with a flat score.</summary>
    public const int MaxQueryChars = 64;

    internal const int MatchScore = 16;
    internal const int ConsecutiveBonus = 10;
    internal const int BoundaryBonus = 8;
    internal const int CamelBonus = 6;
    internal const int GapPenalty = 1;
    internal const int LeadingGapPenalty = 2;
    internal const int MaxLeadingPenalty = 12;

    /// <summary>A match that only exists beyond <see cref="MaxTextChars"/>: kept, but ranked below every real one.</summary>
    internal const int TruncatedMatchScore = -10_000;

    private const int NoScore = int.MinValue / 4;   // still leaves room to add a bonus without overflowing

    /// <summary>
    /// The score of <paramref name="query"/> against <paramref name="text"/>, or null when the query's characters
    /// don't appear in order. An empty query matches everything with score 0.
    /// </summary>
    public static int? Score(ReadOnlySpan<char> text, ReadOnlySpan<char> query)
    {
        if (query.Length == 0)
        {
            return 0;
        }

        if (query.Length > text.Length || !IsSubsequence(text, query))
        {
            return null;
        }

        if (query.Length > MaxQueryChars)
        {
            return 0;   // it matches; ranking a query this long is not worth a millisecond
        }

        if (text.Length > MaxTextChars)
        {
            text = text[..MaxTextChars];
            if (!IsSubsequence(text, query))
            {
                return TruncatedMatchScore;
            }
        }

        return ScoreCore(text, query);
    }

    /// <summary>True when every character of <paramref name="query"/> appears in <paramref name="text"/>, in order.</summary>
    internal static bool IsSubsequence(ReadOnlySpan<char> text, ReadOnlySpan<char> query)
    {
        int q = 0;
        for (int i = 0; i < text.Length && q < query.Length; i++)
        {
            if (Same(text[i], query[q]))
            {
                q++;
            }
        }

        return q == query.Length;
    }

    /// <summary>
    /// best[i] holds the best total score for a match of the query so far whose last character is text[i].
    /// Row j is built from row j-1 with a running maximum that decays by <see cref="GapPenalty"/> per skipped
    /// character, so the whole row costs one pass.
    /// </summary>
    private static int ScoreCore(ReadOnlySpan<char> text, ReadOnlySpan<char> query)
    {
        int length = text.Length;   // capped at MaxTextChars by the caller, so both rows fit on the stack
        Span<int> previous = stackalloc int[length];
        Span<int> current = stackalloc int[length];

        for (int i = 0; i < length; i++)
        {
            previous[i] = Same(text[i], query[0])
                ? CharacterScore(text, i) - Math.Min(LeadingGapPenalty * i, MaxLeadingPenalty)
                : NoScore;
        }

        for (int j = 1; j < query.Length; j++)
        {
            int run = NoScore;   // best row j-1 value seen so far, already charged for the gap up to here
            for (int i = 0; i < length; i++)
            {
                if (Same(text[i], query[j]))
                {
                    int score = CharacterScore(text, i);
                    int fromGap = run == NoScore ? NoScore : run + score;
                    int fromRun = i > 0 && previous[i - 1] != NoScore ? previous[i - 1] + score + ConsecutiveBonus : NoScore;
                    current[i] = Math.Max(fromGap, fromRun);
                }
                else
                {
                    current[i] = NoScore;
                }

                run = Math.Max(run == NoScore ? NoScore : run - GapPenalty, previous[i]);
            }

            current.CopyTo(previous);
        }

        int best = NoScore;
        for (int i = 0; i < length; i++)
        {
            best = Math.Max(best, previous[i]);
        }

        return best;
    }

    /// <summary>What matching text[<paramref name="index"/>] is worth on its own (position bonuses included).</summary>
    private static int CharacterScore(ReadOnlySpan<char> text, int index)
    {
        if (index == 0)
        {
            return MatchScore + BoundaryBonus;
        }

        char previous = text[index - 1];
        if (IsBoundary(previous))
        {
            return MatchScore + BoundaryBonus;
        }

        if (char.IsLower(previous) && char.IsUpper(text[index]))
        {
            return MatchScore + CamelBonus;
        }

        return MatchScore;
    }

    private static bool IsBoundary(char value) =>
        value is '/' or '\\' or '_' or '-' or '.' or ' ' or '(' or '[' or '#' or '@' || char.IsWhiteSpace(value);

    private static bool Same(char left, char right) =>
        left == right || char.ToLowerInvariant(left) == char.ToLowerInvariant(right);
}
