namespace MdReader.Core.Documents;

/// <summary>
/// Ticks or unticks the task-list marker on one source line (ARCHITECTURE §4.4). The rendered checkboxes carry the line
/// they came from (<c>data-line</c>, §4.1); this turns such a click back into an edit of the Markdown.
/// </summary>
/// <remarks>
/// Only the one marker character changes: the rest of the file — indentation, bullet style, trailing spaces, line endings,
/// whatever follows — is copied through unchanged. A render can be stale (the file changed under it), so a line that no
/// longer looks like a task item, or doesn't exist any more, is refused rather than guessed at.
/// </remarks>
public static class TaskListToggle
{
    /// <summary>
    /// Sets the marker of the task item on <paramref name="line"/> (1-based, counting <c>\n</c>, <c>\r\n</c> and lone
    /// <c>\r</c> the way Markdig does) to <paramref name="isChecked"/>.
    /// </summary>
    /// <returns>
    /// True — and <paramref name="updated"/> is the new text, equal to <paramref name="markdown"/> when the marker
    /// already had that state. False when the line is out of range or isn't a task item any more; then
    /// <paramref name="updated"/> is <paramref name="markdown"/>.
    /// </returns>
    public static bool TryToggle(string markdown, int line, bool isChecked, out string updated)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        updated = markdown;
        if (line < 1)
        {
            return false;
        }

        if (!TryGetLine(markdown, line, out var start, out var end))
        {
            return false;
        }

        var marker = FindMarker(markdown.AsSpan(start, end - start));
        if (marker < 0)
        {
            return false;
        }

        var index = start + marker;
        var wanted = isChecked ? 'x' : ' ';
        if (markdown[index] == wanted)
        {
            return true;
        }

        updated = string.Concat(markdown.AsSpan(0, index), wanted.ToString(), markdown.AsSpan(index + 1));
        return true;
    }

    /// <summary>Character range of the 1-based <paramref name="line"/>, excluding its line break.</summary>
    private static bool TryGetLine(string text, int line, out int start, out int end)
    {
        start = 0;
        end = 0;
        var current = 1;
        var i = 0;
        while (true)
        {
            var lineStart = i;
            while (i < text.Length && text[i] != '\n' && text[i] != '\r')
            {
                i++;
            }

            if (current == line)
            {
                start = lineStart;
                end = i;
                return true;
            }

            if (i >= text.Length)
            {
                return false;
            }

            // \r\n counts as one break; a lone \r ends a line too (Markdig's line reader).
            i += text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
            current++;
        }
    }

    /// <summary>
    /// Index of the character between the brackets of the line's task marker, or -1 if the line isn't a task item:
    /// optional indentation, a bullet (<c>-</c>, <c>+</c>, <c>*</c>) or an ordered marker (<c>1.</c>, <c>1)</c>), at least
    /// one space or tab, then <c>[ ]</c>, <c>[x]</c> or <c>[X]</c> followed by a space, a tab or the end of the line.
    /// </summary>
    private static int FindMarker(ReadOnlySpan<char> lineText)
    {
        var i = 0;
        while (i < lineText.Length && (lineText[i] == ' ' || lineText[i] == '\t'))
        {
            i++;
        }

        if (i == lineText.Length)
        {
            return -1;
        }

        if (lineText[i] is '-' or '+' or '*')
        {
            i++;
        }
        else if (char.IsAsciiDigit(lineText[i]))
        {
            var digits = 0;
            while (i < lineText.Length && char.IsAsciiDigit(lineText[i]) && digits < 9)
            {
                i++;
                digits++;
            }

            if (i >= lineText.Length || (lineText[i] != '.' && lineText[i] != ')'))
            {
                return -1;
            }

            i++;
        }
        else
        {
            return -1;
        }

        if (i >= lineText.Length || (lineText[i] != ' ' && lineText[i] != '\t'))
        {
            return -1;
        }

        while (i < lineText.Length && (lineText[i] == ' ' || lineText[i] == '\t'))
        {
            i++;
        }

        if (i + 2 >= lineText.Length || lineText[i] != '[' || lineText[i + 2] != ']')
        {
            return -1;
        }

        if (lineText[i + 1] is not (' ' or 'x' or 'X'))
        {
            return -1;
        }

        var after = i + 3;
        if (after < lineText.Length && lineText[after] != ' ' && lineText[after] != '\t')
        {
            return -1;
        }

        return i + 1;
    }
}
