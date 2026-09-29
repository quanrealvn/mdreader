using System.Buffers;
using System.Text;

namespace MdReader.Core.Json;

/// <summary>
/// The one place author-controlled text becomes output. Everything a JSON document contains — every key, every string,
/// every number, and the raw source in the fallback views — reaches the page through a method here, and every method
/// here writes a text node and nothing else.
/// </summary>
/// <remarks>
/// <para><strong>Escaping matches <c>SanitizedHtmlWriter</c> exactly</strong> (text escapes <c>&amp; &lt; &gt;</c> and
/// U+00A0, nothing else), because the tree's HTML is required to be a fixed point of the sanitizer:
/// <c>Sanitize(html) == html</c>, asserted over the whole test corpus in <c>JsonSecurityTests</c>. Escaping a
/// character the writer leaves alone (<c>"</c>, say) would be just as safe but would break that equality, and with it
/// the proof that routing this HTML through the sanitizer is a no-op.</para>
/// <para>Author text is never written into an attribute. The only attribute values the tree emits are literals in the
/// source and ordinal ids of our own making, so there is no attribute-escaping path to get wrong.</para>
/// </remarks>
internal static class JsonText
{
    private const char NoBreakSpace = (char)0x00A0;
    private const char Delete = (char)0x007F;
    private const char C1Start = (char)0x0080;
    private const char C1End = (char)0x009F;
    private const char LineSeparator = (char)0x2028;
    private const char ParagraphSeparator = (char)0x2029;
    private const char Replacement = (char)0xFFFD;

    /// <summary>The characters <c>SanitizedHtmlWriter</c> escapes in a text node.</summary>
    private static readonly SearchValues<char> HtmlTextSpecials = SearchValues.Create(['&', '<', '>', NoBreakSpace]);

    private const string HexDigits = "0123456789abcdef";

    /// <summary>Appends <paramref name="text"/> as HTML text (no markup can result).</summary>
    public static void AppendHtml(StringBuilder output, ReadOnlySpan<char> text)
    {
        while (true)
        {
            var index = text.IndexOfAny(HtmlTextSpecials);
            if (index < 0)
            {
                output.Append(text);
                return;
            }

            output.Append(text[..index]);
            output.Append(text[index] switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                _ => "&nbsp;",
            });

            text = text[(index + 1)..];
        }
    }

    /// <summary>
    /// Appends <paramref name="value"/> — a decoded JSON string or property name — as HTML text, re-escaped the way
    /// JSON writes it, so that a value holding a newline, a tab or a control character stays on its own line and stays
    /// legible. Nothing is interpreted: <c>café</c> shows as <c>café</c> because that *is* the value, and
    /// <c>"1e3"</c> stays the four characters it is.
    /// </summary>
    /// <param name="maxLength">Characters of <paramref name="value"/> to show before eliding the rest.</param>
    /// <returns>The number of characters elided, or 0 if the whole value was written.</returns>
    public static int AppendStringBody(StringBuilder output, string value, int maxLength)
    {
        var length = value.Length;
        var shown = length;
        if (length > maxLength)
        {
            shown = maxLength;

            // Never cut between the halves of a surrogate pair: half a pair is not a character.
            if (char.IsHighSurrogate(value[shown - 1]))
            {
                shown--;
            }
        }

        for (var i = 0; i < shown; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '"':
                    output.Append("\\\"");
                    break;
                case '\\':
                    output.Append("\\\\");
                    break;
                case '\b':
                    output.Append("\\b");
                    break;
                case '\f':
                    output.Append("\\f");
                    break;
                case '\n':
                    output.Append("\\n");
                    break;
                case '\r':
                    output.Append("\\r");
                    break;
                case '\t':
                    output.Append("\\t");
                    break;
                case '&':
                    output.Append("&amp;");
                    break;
                case '<':
                    output.Append("&lt;");
                    break;
                case '>':
                    output.Append("&gt;");
                    break;
                case NoBreakSpace:
                    output.Append("&nbsp;");
                    break;
                default:
                    if (NeedsUnicodeEscape(value, i, c))
                    {
                        AppendUnicodeEscape(output, c);
                    }
                    else
                    {
                        output.Append(c);
                    }

                    break;
            }
        }

        return length - shown;
    }

    /// <summary>
    /// Appends <paramref name="source"/> as the body of a <c>pre</c> element: the document's own text, escaped.
    /// </summary>
    /// <remarks>
    /// Line breaks are normalised to <c>\n</c> and NUL to U+FFFD because the HTML parser does exactly that to any
    /// markup it is handed, so emitting anything else would mean the page shows something other than what was written
    /// here — and would stop the output being a fixed point of the sanitizer.
    /// </remarks>
    public static void AppendSourceText(StringBuilder output, string source)
    {
        var start = 0;
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c is not ('\r' or '\0'))
            {
                continue;
            }

            AppendHtml(output, source.AsSpan(start, i - start));
            if (c == '\0')
            {
                output.Append(Replacement);
            }
            else
            {
                output.Append('\n');
                if (i + 1 < source.Length && source[i + 1] == '\n')
                {
                    i++;
                }
            }

            start = i + 1;
        }

        AppendHtml(output, source.AsSpan(start));
    }

    /// <summary>
    /// Opens a <c>pre</c> whose text is exactly <paramref name="content"/>. The HTML parser drops one newline
    /// immediately after the start tag, so one is added back when the content begins with one — and only then, so that
    /// re-parsing and re-serializing the output reproduces it character for character.
    /// </summary>
    public static void OpenPre(StringBuilder output, string cssClass, string content)
    {
        output.Append("<pre class=\"").Append(cssClass).Append("\">");
        if (content.StartsWith('\n') || content.StartsWith('\r'))
        {
            output.Append('\n');
        }
    }

    /// <summary>
    /// True for a character that would be invisible, would disturb the line, or is half of a broken surrogate pair.
    /// C1 controls and the Unicode line separators are included: both are invisible in a browser, and U+2028 ends a
    /// line in some contexts, which would silently split a value across two rows of the tree.
    /// </summary>
    private static bool NeedsUnicodeEscape(string value, int index, char c)
    {
        if (c < ' ' || c is Delete or LineSeparator or ParagraphSeparator || (c >= C1Start && c <= C1End))
        {
            return true;
        }

        if (char.IsHighSurrogate(c))
        {
            return index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]);
        }

        return char.IsLowSurrogate(c) && (index == 0 || !char.IsHighSurrogate(value[index - 1]));
    }

    private static void AppendUnicodeEscape(StringBuilder output, char c)
    {
        output.Append("\\u")
            .Append(HexDigits[(c >> 12) & 0xF])
            .Append(HexDigits[(c >> 8) & 0xF])
            .Append(HexDigits[(c >> 4) & 0xF])
            .Append(HexDigits[c & 0xF]);
    }
}
