using System.Text;

namespace MdReader.Web;

/// <summary>Escaping for the text the page template writes around the rendered Markdown.</summary>
internal static class HtmlText
{
    /// <summary>
    /// Escapes text for both element content and a double-quoted attribute value. <c>&amp;</c> goes first, or the
    /// escapes produced after it would be escaped again.
    /// </summary>
    /// <remarks>
    /// An apostrophe is deliberately left alone. Every attribute the template writes is double-quoted, so
    /// <c>&amp;#39;</c> would only make a meta description harder to read for no gain in safety.
    /// </remarks>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (text.AsSpan().IndexOfAny("&<>\"") < 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length + 16);
        foreach (var c in text)
        {
            _ = c switch
            {
                '&' => builder.Append("&amp;"),
                '<' => builder.Append("&lt;"),
                '>' => builder.Append("&gt;"),
                '"' => builder.Append("&quot;"),
                _ => builder.Append(c),
            };
        }

        return builder.ToString();
    }
}
