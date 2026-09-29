using System.Globalization;

namespace MdReader.Core.Json;

/// <summary>
/// Where a JSON document stopped being JSON, and what to say about it (JSON-PLAN, "Errors").
/// </summary>
/// <param name="Line">1-based line of the offending character.</param>
/// <param name="Column">1-based column, counted in characters rather than UTF-8 bytes — which is what an editor shows
/// and what a reader can count to.</param>
/// <param name="Message">One sentence in plain words, naming the fix where there is one. No position: that is
/// <see cref="Summary"/>'s job, so a caller can place the two independently.</param>
/// <param name="Excerpt">The offending line, tabs flattened to single spaces and a window cut out of it when it is
/// long — a minified document is one line of several megabytes, and the useful part of it is the part around the
/// error. An elision is marked with <c>…</c> at that end.</param>
/// <param name="CaretOffset">0-based index into <see cref="Excerpt"/> of the character the caret points at.</param>
public sealed record JsonParseError(int Line, int Column, string Message, string Excerpt, int CaretOffset)
{
    /// <summary>"Line 3, column 9: a trailing comma …".</summary>
    public string Summary =>
        string.Format(CultureInfo.InvariantCulture, "Line {0}, column {1}: {2}", Line, Column, Message);

    /// <summary>The excerpt with the caret line under it, as a terminal or a log would show it.</summary>
    public string Caret => new string(' ', CaretOffset) + '^';
}
