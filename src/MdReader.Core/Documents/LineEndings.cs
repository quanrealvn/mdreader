namespace MdReader.Core.Documents;

/// <summary>
/// Line-ending bookkeeping for the editor (ARCHITECTURE §4.4). A WPF text box always inserts <c>\r\n</c> when the user
/// presses Enter, so the buffer is held in <c>\r\n</c> and converted back to the file's own line ending when it is saved
/// or rendered. That keeps an LF document LF instead of turning it into a mixed one at the first keystroke.
/// </summary>
public static class LineEndings
{
    public const string Crlf = "\r\n";
    public const string Lf = "\n";
    public const string Cr = "\r";

    /// <summary>
    /// The line ending the text mostly uses: <c>\r\n</c> unless plain <c>\n</c> (or lone <c>\r</c>) is more common. Text
    /// without any line break counts as <c>\r\n</c> — MdReader runs on Windows and a one-line file gives no evidence.
    /// </summary>
    public static string Detect(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var crlf = 0;
        var lf = 0;
        var cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '\r' when i + 1 < text.Length && text[i + 1] == '\n':
                    crlf++;
                    i++;
                    break;
                case '\r':
                    cr++;
                    break;
                case '\n':
                    lf++;
                    break;
            }
        }

        if (lf > crlf && lf >= cr)
        {
            return Lf;
        }

        return cr > crlf && cr > lf ? Cr : Crlf;
    }

    /// <summary>Every line ending as <c>\r\n</c> (what a WPF text box produces).</summary>
    public static string ToCrlf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Convert(text, Crlf);
    }

    /// <summary>Every line ending as <paramref name="lineEnding"/>.</summary>
    public static string Convert(string text, string lineEnding)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(lineEnding);

        if (!NeedsConversion(text, lineEnding))
        {
            return text;
        }

        var builder = new System.Text.StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r')
            {
                builder.Append(lineEnding);
                if (i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }
            }
            else if (c == '\n')
            {
                builder.Append(lineEnding);
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static bool NeedsConversion(string text, string lineEnding)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\r' && c != '\n')
            {
                continue;
            }

            var length = c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
            if (length != lineEnding.Length || !text.AsSpan(i, length).SequenceEqual(lineEnding))
            {
                return true;
            }

            i += length - 1;
        }

        return false;
    }
}
