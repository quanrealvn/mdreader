using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MdReader.Core.Json;

/// <summary>
/// What the parser knew when it gave up. Enough to say something better than "unexpected token": which container was
/// still open and where it started, and whether a property name or a value was due next.
/// </summary>
internal readonly record struct JsonErrorContext(
    bool AnyToken,
    bool InsideObject,
    bool ExpectingPropertyName,
    char OpenContainer,
    int OpenContainerLine);

/// <summary>
/// Turns a <see cref="JsonException"/> into something a reader can act on (JSON-PLAN, "Errors"): the line, the column
/// counted in characters, the offending line with a caret under the character, and a sentence that names the fix.
/// </summary>
/// <remarks>
/// The wording is decided by looking at the document at the failing position rather than by matching on
/// <c>System.Text.Json</c>'s message, which is an implementation detail of the runtime and changes between versions.
/// The three failures people actually hit — a trailing comma, single quotes, an unquoted key — each get their own
/// sentence; anything else falls back to the runtime's own words with its position suffix stripped, because that
/// position is shown properly instead.
/// </remarks>
internal static class JsonErrors
{
    /// <summary>Characters of the offending line to show.</summary>
    private const int MaxExcerptLength = 120;

    /// <summary>Characters of the offending line to keep before the caret when the line has to be cut.</summary>
    private const int CaretMargin = 48;

    /// <summary>Bytes either side of the caret that are decoded at all. A minified document is one very long line.</summary>
    private const int DecodeMargin = 512;

    /// <summary>Lines of a line-delimited file to try before deciding it is one.</summary>
    private const int JsonLinesSampleSize = 5;

    /// <summary>The longest line that is worth test-parsing when looking for JSON Lines.</summary>
    private const int JsonLinesMaxLineLength = 1 << 20;

    public static JsonParseError FromException(ReadOnlySpan<byte> utf8, JsonException exception, in JsonErrorContext context)
    {
        var offset = OffsetOf(utf8, exception);
        return Build(utf8, offset, Diagnose(utf8, offset, context, exception.Message));
    }

    /// <summary>An error we diagnose ourselves, at a byte offset we already hold.</summary>
    public static JsonParseError At(ReadOnlySpan<byte> utf8, int offset, string message) =>
        Build(utf8, Math.Clamp(offset, 0, utf8.Length), message);

    /// <summary>The document ran out before the value did.</summary>
    public static JsonParseError AtEnd(ReadOnlySpan<byte> utf8, in JsonErrorContext context) =>
        Build(utf8, utf8.Length, AtEndOfFile(utf8, context));

    /// <summary>The absolute byte offset an exception's line and position refer to.</summary>
    public static int OffsetOf(ReadOnlySpan<byte> utf8, JsonException exception)
    {
        var lineStart = StartOfLine(utf8, (int)Math.Clamp(exception.LineNumber ?? 0, 0, int.MaxValue));
        var positionInLine = Math.Clamp(exception.BytePositionInLine ?? 0, 0, int.MaxValue);
        return (int)Math.Clamp(lineStart + positionInLine, 0, utf8.Length);
    }

    /// <summary>
    /// True if the document looks like JSON Lines: several lines, each a complete JSON value of its own. Worth saying
    /// out loud, because "expected end of data" is a baffling thing to be told about a file that is full of valid JSON.
    /// </summary>
    public static bool LooksLikeJsonLines(ReadOnlySpan<byte> utf8)
    {
        var complete = 0;
        var examined = 0;
        var rest = utf8;
        while (examined < JsonLinesSampleSize && !rest.IsEmpty)
        {
            var newline = rest.IndexOf((byte)'\n');
            var line = newline < 0 ? rest : rest[..newline];
            rest = newline < 0 ? default : rest[(newline + 1)..];
            if (IsWhitespace(line))
            {
                continue;
            }

            examined++;
            if (!IsCompleteValue(line))
            {
                return false;
            }

            complete++;
        }

        return complete >= 2;
    }

    private static string Diagnose(ReadOnlySpan<byte> utf8, int offset, in JsonErrorContext context, string runtimeMessage)
    {
        if (offset >= utf8.Length)
        {
            return AtEndOfFile(utf8, context);
        }

        var c = utf8[offset];
        switch (c)
        {
            case (byte)'\'':
                return "a string has to be written in double quotes, and this one uses single quotes.";

            case (byte)'/':
                return "JSON has no comments, so this is read as the start of a value.";

            case (byte)'}':
            case (byte)']':
                if (PreviousNonWhitespace(utf8, offset) == (byte)',')
                {
                    return "a trailing comma is not allowed in JSON. Remove the comma before this "
                           + (char)c + ".";
                }

                // The wrong bracket: the useful place to look is where the container that is still open began.
                if (c == (byte)']' && context.OpenContainer == '{')
                {
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "this ] closes an array, but the object opened on line {0} is still open.",
                        context.OpenContainerLine);
                }

                if (c == (byte)'}' && context.OpenContainer == '[')
                {
                    return string.Format(
                        CultureInfo.InvariantCulture,
                        "this }} closes an object, but the array opened on line {0} is still open.",
                        context.OpenContainerLine);
                }

                break;

            case (byte)'{':
            case (byte)'[':
                if (context.ExpectingPropertyName)
                {
                    return "a property name was expected here, and a property name has to be a string in double quotes.";
                }

                break;

            default:
                if (char.IsAsciiLetter((char)c) || c is (byte)'_' or (byte)'$')
                {
                    return context.ExpectingPropertyName
                        ? "a property name has to be a string in double quotes."
                        : "a value has to be a string in double quotes, a number, true, false or null.";
                }

                break;
        }

        return Runtime(runtimeMessage);
    }

    private static string AtEndOfFile(ReadOnlySpan<byte> utf8, in JsonErrorContext context)
    {
        if (!context.AnyToken)
        {
            return IsWhitespace(utf8)
                ? "this file is empty, and an empty file is not a JSON document."
                : "this file does not start with a JSON value.";
        }

        if (context.OpenContainer is '{' or '[')
        {
            var kind = context.OpenContainer == '{' ? "object" : "array";
            return string.Format(
                CultureInfo.InvariantCulture,
                "the file ends while the {0} opened on line {1} is still open.",
                kind,
                context.OpenContainerLine);
        }

        return "the file ends in the middle of a value.";
    }

    /// <summary>
    /// The runtime's own sentence, with the position suffix it appends (" Path: $.a | LineNumber: 2 | …") removed,
    /// because the position is shown properly above the caret instead.
    /// </summary>
    private static string Runtime(string message)
    {
        var cut = message.IndexOf(" Path:", StringComparison.Ordinal);
        if (cut < 0)
        {
            cut = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        }

        var text = (cut < 0 ? message : message[..cut]).Trim();
        if (text.Length == 0)
        {
            return "this is not valid JSON.";
        }

        return text.EndsWith('.') ? text : text + ".";
    }

    private static JsonParseError Build(ReadOnlySpan<byte> utf8, int offset, string message)
    {
        var (lineIndex, lineStart, lineEnd) = Locate(utf8, offset);
        var caret = Math.Clamp(offset, lineStart, lineEnd);

        // Decode only a window around the caret: a minified document is a single line of megabytes, and only the part
        // around the error is any use. The window is nudged onto UTF-8 character boundaries so nothing is cut in half.
        var windowStart = Math.Min(BoundaryAtOrAfter(utf8, Math.Max(lineStart, caret - DecodeMargin)), caret);
        var windowEnd = Math.Max(BoundaryAtOrBefore(utf8, Math.Min(lineEnd, caret + DecodeMargin), lineStart), caret);

        var text = Flatten(Encoding.UTF8.GetString(utf8[windowStart..windowEnd]));
        var caretIndex = Encoding.UTF8.GetCharCount(utf8[windowStart..caret]);
        var elidedLeft = windowStart > lineStart;
        var elidedRight = windowEnd < lineEnd;

        // The column is the character's position in the whole line, not in the window.
        var column = Encoding.UTF8.GetCharCount(utf8[lineStart..caret]) + 1;

        if (text.Length > MaxExcerptLength)
        {
            var start = Math.Clamp(caretIndex - CaretMargin, 0, text.Length - MaxExcerptLength);
            elidedLeft |= start > 0;
            elidedRight |= start + MaxExcerptLength < text.Length;
            text = text.Substring(start, MaxExcerptLength);
            caretIndex -= start;
        }

        var excerpt = new StringBuilder(text.Length + 2);
        if (elidedLeft)
        {
            excerpt.Append('…');
            caretIndex++;
        }

        excerpt.Append(text);
        if (elidedRight)
        {
            excerpt.Append('…');
        }

        return new JsonParseError(lineIndex + 1, column, message, excerpt.ToString(),
            Math.Clamp(caretIndex, 0, excerpt.Length));
    }

    /// <summary>Every control character becomes one space, so the excerpt stays on one line and the caret still lines up.</summary>
    private static string Flatten(string text)
    {
        char[]? buffer = null;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] >= ' ')
            {
                continue;
            }

            buffer ??= text.ToCharArray();
            buffer[i] = ' ';
        }

        return buffer is null ? text : new string(buffer);
    }

    /// <summary>The 0-based line of <paramref name="offset"/> and the byte range of that line, excluding its newline.</summary>
    private static (int Line, int Start, int End) Locate(ReadOnlySpan<byte> utf8, int offset)
    {
        var line = 0;
        var start = 0;
        for (var i = 0; i < offset; i++)
        {
            if (utf8[i] == (byte)'\n')
            {
                line++;
                start = i + 1;
            }
        }

        var newline = utf8[start..].IndexOf((byte)'\n');
        var end = newline < 0 ? utf8.Length : start + newline;
        if (end > start && utf8[end - 1] == (byte)'\r')
        {
            end--;
        }

        return (line, start, end);
    }

    private static int StartOfLine(ReadOnlySpan<byte> utf8, int lineIndex)
    {
        var start = 0;
        for (var line = 0; line < lineIndex; line++)
        {
            var newline = utf8[start..].IndexOf((byte)'\n');
            if (newline < 0)
            {
                return utf8.Length;
            }

            start += newline + 1;
        }

        return start;
    }

    private static byte PreviousNonWhitespace(ReadOnlySpan<byte> utf8, int offset)
    {
        for (var i = offset - 1; i >= 0; i--)
        {
            if (utf8[i] is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return utf8[i];
            }
        }

        return 0;
    }

    private static bool IsContinuation(byte b) => (b & 0xC0) == 0x80;

    private static int BoundaryAtOrAfter(ReadOnlySpan<byte> utf8, int offset)
    {
        while (offset < utf8.Length && IsContinuation(utf8[offset]))
        {
            offset++;
        }

        return offset;
    }

    private static int BoundaryAtOrBefore(ReadOnlySpan<byte> utf8, int offset, int floor)
    {
        while (offset > floor && offset < utf8.Length && IsContinuation(utf8[offset]))
        {
            offset--;
        }

        return offset;
    }

    private static bool IsWhitespace(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True if <paramref name="line"/> is one complete JSON value and nothing else.</summary>
    private static bool IsCompleteValue(ReadOnlySpan<byte> line)
    {
        if (line.Length > JsonLinesMaxLineLength)
        {
            return false;
        }

        while (!line.IsEmpty && line[^1] is (byte)' ' or (byte)'\t' or (byte)'\r')
        {
            line = line[..^1];
        }

        try
        {
            var reader = new Utf8JsonReader(line, new JsonReaderOptions { MaxDepth = 64 });
            var any = false;
            while (reader.Read())
            {
                any = true;
            }

            return any && reader.CurrentDepth == 0 && reader.BytesConsumed == line.Length;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
