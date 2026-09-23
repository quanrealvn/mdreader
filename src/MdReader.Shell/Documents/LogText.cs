using System.Globalization;
using System.Text;

namespace MdReader.Shell.Documents;

/// <summary>Escaping for untrusted text that ends up in a log line (document content, hrefs, URLs, parser errors).</summary>
public static class LogText
{
    private const int MaxLoggedUriChars = 300;

    /// Untrusted URL/href text for a log line: escaped (see <see cref="ForLog"/>) and capped at 300 chars.
    public static string Shorten(string? value) => ForLog(value, MaxLoggedUriChars);

    /// <summary>
    /// Makes untrusted text safe for ONE log line: CR/LF/tab and every other control character (C0, DEL, C1),
    /// line/paragraph separators and bidi overrides are escaped, and the result is capped, so content can never forge
    /// or disguise log lines.
    /// </summary>
    public static string ForLog(string? value, int maxChars = 2000)
    {
        if (value is null)
        {
            return "(null)";
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxChars) + 8);
        foreach (char c in value)
        {
            if (builder.Length >= maxChars)
            {
                builder.Append('…');
                break;
            }

            switch (c)
            {
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (char.IsControl(c) || IsInvisibleFormatting(c))
                    {
                        builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    /// Line/paragraph separators (U+2028, U+2029) and bidi embedding/override/isolate controls (U+202A..U+202E,
    /// U+2066..U+2069): not char.IsControl, but they break lines or visually reorder a log line. Numeric on purpose,
    /// so the source file stays plain ASCII.
    private static bool IsInvisibleFormatting(char c) =>
        c == (char)0x2028 || c == (char)0x2029
        || (c >= (char)0x202A && c <= (char)0x202E)
        || (c >= (char)0x2066 && c <= (char)0x2069);
}
