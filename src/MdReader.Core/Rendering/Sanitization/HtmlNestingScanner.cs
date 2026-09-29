using System.Collections.Frozen;

namespace MdReader.Core.Rendering.Sanitization;

/// <summary>
/// Linear pre-scan of the HTML handed to the sanitizer: estimates the element nesting depth AngleSharp would build, so
/// pathological nesting (<see cref="RenderLimits.MaxHtmlNesting"/>) never reaches its quadratic tree construction.
/// </summary>
/// <remarks>
/// <para>A simplified HTML tokenizer + tree model: start tags push, end tags pop down to the nearest open element of that
/// name (unmatched end tags are ignored). Wherever it simplifies, it errs on the side of <em>over</em>-counting — a false
/// positive only costs the plain-text fallback — and it never skips markup the real parser would build:</para>
/// <list type="bullet">
/// <item>void elements and <c>p tr td th tbody thead tfoot colgroup caption option</c> are not counted: they close
/// themselves implicitly and can only nest through a counted element;</item>
/// <item><c>li</c>, <c>dd</c>, <c>dt</c> are counted, with the parser's implicit close emulated (a new one closes the
/// previous one unless a "special" element other than <c>address div p</c> is in between). Not counting them would
/// miss <c>&lt;li&gt;&lt;dd&gt;&lt;li&gt;&lt;dd&gt;…</c>, which nests without limit;</item>
/// <item><c>rt</c>/<c>rp</c> are exempt only directly inside <c>ruby</c> (behind a scope boundary they do nest);</item>
/// <item>unclosed formatting elements (<c>b</c>, <c>i</c>, …) are counted;</item>
/// <item><c>/&gt;</c> self-closes only where the parser honors it: <c>svg</c>/<c>math</c> themselves, and elements in
/// foreign content (inside <c>svg</c>/<c>math</c>, but not in an HTML integration point such as <c>foreignObject</c> or
/// <c>mtext</c>). HTML elements ignore it (<c>&lt;div/&gt;</c> opens a div). The self-closing flag is detected like the
/// tokenizer does (<c>&lt;g x=a/&gt;</c> is not self-closing: the value is <c>a/</c>). Breakout tags (<c>div</c>,
/// <c>p</c>, <c>b</c>, <c>table</c>, <c>font</c>, …) and <c>&lt;/p&gt;</c>/<c>&lt;/br&gt;</c> leave foreign content, as in
/// the parser;</item>
/// <item>comments end at <c>--&gt;</c>, <c>--!&gt;</c>, <c>&lt;!--&gt;</c> or <c>&lt;!---&gt;</c>; other <c>&lt;!…</c> /
/// <c>&lt;?…</c> constructs at the first <c>&gt;</c>, as in the tokenizer;</item>
/// <item>raw-text/RCDATA elements (<c>script style xmp iframe noembed noframes noscript textarea title</c>; the sanitizer
/// parses with scripting enabled) are skipped to their first end tag and <c>plaintext</c> ends the markup, but only as long
/// as no <c>svg</c>/<c>math</c> has been seen: after that their content is scanned as markup, so a divergence between
/// this model and the parser about foreign content can only over-count.</item>
/// </list>
/// </remarks>
internal static class HtmlNestingScanner
{
    /// <summary>How far down the stack the li/dd/dt implicit-close search looks (bounded work; not finding = push).</summary>
    private const int ImplicitCloseSearchLimit = 64;

    private static readonly FrozenSet<string> NotCounted = Set(
        // void elements
        "area", "base", "basefont", "bgsound", "br", "col", "embed", "frame", "hr", "image", "img", "input", "keygen",
        "link", "meta", "param", "source", "track", "wbr",

        // optional end tags that can't nest without a counted element in between
        "p", "tr", "td", "th", "tbody", "thead", "tfoot", "colgroup", "caption", "option");

    private static readonly FrozenSet<string> RawText = Set(
        "script", "style", "xmp", "iframe", "noembed", "noframes", "noscript", "textarea", "title");

    /// <summary>The HTML parser's "special" category (HTML, MathML and SVG names).</summary>
    private static readonly FrozenSet<string> Special = Set(
        "address", "applet", "area", "article", "aside", "base", "basefont", "bgsound", "blockquote", "body", "br",
        "button", "caption", "center", "col", "colgroup", "dd", "details", "dir", "div", "dl", "dt", "embed", "fieldset",
        "figcaption", "figure", "footer", "form", "frame", "frameset", "h1", "h2", "h3", "h4", "h5", "h6", "head",
        "header", "hgroup", "hr", "html", "iframe", "img", "input", "keygen", "li", "link", "listing", "main", "marquee",
        "menu", "meta", "nav", "noembed", "noframes", "noscript", "object", "ol", "p", "param", "plaintext", "pre",
        "script", "search", "section", "select", "source", "style", "summary", "table", "tbody", "td", "template",
        "textarea", "tfoot", "th", "thead", "title", "tr", "track", "ul", "wbr", "xmp",
        "mi", "mo", "mn", "ms", "mtext", "annotation-xml", "foreignobject", "desc");

    /// <summary>
    /// Start tags that break out of foreign content. <c>font</c> only does with a color/face/size attribute; treating it
    /// as a breakout always can only over-count.
    /// </summary>
    private static readonly FrozenSet<string> Breakout = Set(
        "b", "big", "blockquote", "body", "br", "center", "code", "dd", "div", "dl", "dt", "em", "embed", "h1", "h2", "h3",
        "h4", "h5", "h6", "head", "hr", "i", "img", "li", "listing", "menu", "meta", "nobr", "ol", "p", "pre", "ruby", "s",
        "small", "span", "strong", "strike", "sub", "sup", "table", "tt", "u", "ul", "var", "font");

    /// <summary>Foreign elements whose content is parsed as HTML (SVG and MathML integration points).</summary>
    private static readonly FrozenSet<string> IntegrationPoints = Set(
        "foreignobject", "desc", "title", "mi", "mo", "mn", "ms", "mtext", "annotation-xml");

    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> NotCountedLookup = NotCounted.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> RawTextLookup = RawText.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> BreakoutLookup = Breakout.GetAlternateLookup<ReadOnlySpan<char>>();

    /// <summary>True if the estimated nesting depth of <paramref name="html"/> exceeds <paramref name="maxDepth"/>.</summary>
    public static bool Exceeds(string html, int maxDepth)
    {
        ArgumentNullException.ThrowIfNull(html);
        return new Scan(html, maxDepth).Run();
    }

    /// <summary>The estimated maximum nesting depth (for tests).</summary>
    internal static int MaxDepth(string html)
    {
        var scan = new Scan(html, int.MaxValue);
        scan.Run();
        return scan.MaxDepthSeen;
    }

    private static FrozenSet<string> Set(params string[] names) => names.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsWhitespace(char c) => c is ' ' or '\t' or '\n' or '\f' or '\r';

    private sealed class Scan(string html, int maxDepth)
    {
        // Open elements (names interned through _openCounts, so equal names are the same instance; Foreign = in the SVG
        // or MathML namespace in this model) and how many of each name are open, so an end tag without a matching open
        // element costs O(1).
        private readonly List<(string Name, bool Foreign)> _stack = [];
        private readonly Dictionary<string, int> _openCounts = new(StringComparer.OrdinalIgnoreCase);
        private bool _sawForeign;

        public int MaxDepthSeen { get; private set; }

        /// <summary>The current node is a foreign element that is not an HTML integration point: foreign-content rules.</summary>
        private bool InForeignContent =>
            _stack.Count > 0 && _stack[^1].Foreign && !IntegrationPoints.Contains(_stack[^1].Name);

        /// <summary>True as soon as the depth exceeds the limit.</summary>
        public bool Run()
        {
            var text = html.AsSpan();
            var i = 0;
            while (true)
            {
                var lt = text[i..].IndexOf('<');
                if (lt < 0)
                {
                    return false;
                }

                i += lt + 1;
                if (i >= text.Length)
                {
                    return false;
                }

                var c = text[i];
                if (char.IsAsciiLetter(c))
                {
                    var nameEnd = NameEnd(text, i);
                    var name = text[i..nameEnd];
                    i = TagEnd(text, nameEnd, out var selfClosing);
                    if (i < 0)
                    {
                        return false;                               // EOF inside the tag: the parser drops it
                    }

                    if (StartTag(text, ref i, name, selfClosing))
                    {
                        return true;
                    }

                    if (i < 0)
                    {
                        return false;                               // the rest is text (plaintext, unclosed raw text)
                    }
                }
                else if (c == '/' && i + 1 < text.Length && char.IsAsciiLetter(text[i + 1]))
                {
                    var nameEnd = NameEnd(text, i + 1);
                    EndTag(text[(i + 1)..nameEnd]);
                    i = TagEnd(text, nameEnd, out _);
                    if (i < 0)
                    {
                        return false;
                    }
                }
                else if (c == '!')
                {
                    i = SkipMarkupDeclaration(text, i + 1);
                    if (i < 0)
                    {
                        return false;
                    }
                }
                else if (c is '?' or '/')
                {
                    // Bogus comment ("<?…>", "</ …>", "</>") up to the next '>'.
                    var gt = text[i..].IndexOf('>');
                    if (gt < 0)
                    {
                        return false;
                    }

                    i += gt + 1;
                }

                // Anything else: the '<' was text.
            }
        }

        /// <summary>
        /// Handles a start tag; <paramref name="index"/> is after the tag and may move past raw text (-1: the rest of the
        /// input is text). Returns true once the limit is exceeded.
        /// </summary>
        private bool StartTag(ReadOnlySpan<char> text, ref int index, ReadOnlySpan<char> name, bool selfClosing)
        {
            if (InForeignContent)
            {
                if (!BreakoutLookup.Contains(name))
                {
                    // A foreign element: "/>" really self-closes it.
                    return !selfClosing && Push(name, foreign: true);
                }

                // Breakout: the parser pops back to HTML content and processes the tag as HTML.
                while (InForeignContent)
                {
                    PopTo(_stack.Count - 1);
                }
            }

            if (IsForeignRoot(name))
            {
                _sawForeign = true;
                return !selfClosing && Push(name, foreign: true);         // <svg/> and <math/> self-close in HTML too
            }

            if (!_sawForeign)
            {
                if (name.Equals("plaintext", StringComparison.OrdinalIgnoreCase))
                {
                    index = -1;                                     // everything after it is text
                    return Push(name, foreign: false);
                }

                if (RawTextLookup.Contains(name))
                {
                    index = SkipRawText(text, index, name);
                    return false;
                }
            }

            if (NotCountedLookup.Contains(name) || (IsRubyText(name) && TopIs("ruby")))
            {
                return false;
            }

            if (IsListItem(name))
            {
                CloseImplicitly(name);
            }

            return Push(name, foreign: false);                      // HTML elements ignore "/>"
        }

        private void EndTag(ReadOnlySpan<char> name)
        {
            if (InForeignContent
                && (name.Equals("p", StringComparison.OrdinalIgnoreCase) || name.Equals("br", StringComparison.OrdinalIgnoreCase)))
            {
                // </p> and </br> in foreign content pop back to HTML content (then act on HTML, adding nothing here).
                while (InForeignContent)
                {
                    PopTo(_stack.Count - 1);
                }

                return;
            }

            var counts = _openCounts.GetAlternateLookup<ReadOnlySpan<char>>();
            if (!counts.TryGetValue(name, out var key, out var count) || count == 0)
            {
                return;                                             // no such element open: the parser ignores it
            }

            for (var index = _stack.Count - 1; index >= 0; index--)
            {
                if (ReferenceEquals(_stack[index].Name, key))
                {
                    PopTo(index);
                    return;
                }
            }
        }

        private static bool IsForeignRoot(ReadOnlySpan<char> name) =>
            name.Equals("svg", StringComparison.OrdinalIgnoreCase) || name.Equals("math", StringComparison.OrdinalIgnoreCase);

        private static bool IsRubyText(ReadOnlySpan<char> name) =>
            name.Equals("rt", StringComparison.OrdinalIgnoreCase) || name.Equals("rp", StringComparison.OrdinalIgnoreCase);

        private static bool IsListItem(ReadOnlySpan<char> name) =>
            name.Equals("li", StringComparison.OrdinalIgnoreCase) || name.Equals("dd", StringComparison.OrdinalIgnoreCase)
            || name.Equals("dt", StringComparison.OrdinalIgnoreCase);

        private bool TopIs(string name) => _stack.Count > 0 && _stack[^1].Name.Equals(name, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The parser's rule for a new li (dd/dt): walk down the stack; an li (a dd or dt) is closed with everything above
        /// it; a special element other than address/div/p stops the search.
        /// </summary>
        private void CloseImplicitly(ReadOnlySpan<char> name)
        {
            var isLi = name.Equals("li", StringComparison.OrdinalIgnoreCase);
            var lowest = Math.Max(0, _stack.Count - ImplicitCloseSearchLimit);
            for (var index = _stack.Count - 1; index >= lowest; index--)
            {
                var open = _stack[index].Name;
                var closes = isLi
                    ? open.Equals("li", StringComparison.OrdinalIgnoreCase)
                    : open.Equals("dd", StringComparison.OrdinalIgnoreCase) || open.Equals("dt", StringComparison.OrdinalIgnoreCase);
                if (closes)
                {
                    PopTo(index);
                    return;
                }

                if (Special.Contains(open) && !open.Equals("address", StringComparison.OrdinalIgnoreCase)
                    && !open.Equals("div", StringComparison.OrdinalIgnoreCase) && !open.Equals("p", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
        }

        private bool Push(ReadOnlySpan<char> name, bool foreign)
        {
            var counts = _openCounts.GetAlternateLookup<ReadOnlySpan<char>>();
            if (counts.TryGetValue(name, out var key, out var count))
            {
                _openCounts[key] = count + 1;
            }
            else
            {
                key = name.ToString();
                _openCounts[key] = 1;
            }

            _stack.Add((key, foreign));
            if (_stack.Count > MaxDepthSeen)
            {
                MaxDepthSeen = _stack.Count;
            }

            return _stack.Count > maxDepth;
        }

        /// <summary>Pops the element at <paramref name="index"/> and everything above it.</summary>
        private void PopTo(int index)
        {
            for (var top = _stack.Count - 1; top >= index; top--)
            {
                _openCounts[_stack[top].Name]--;
            }

            _stack.RemoveRange(index, _stack.Count - index);
        }

        /// <summary>A tag name runs to whitespace, '/', or '&gt;'.</summary>
        private static int NameEnd(ReadOnlySpan<char> text, int start)
        {
            var i = start;
            while (i < text.Length && !IsWhitespace(text[i]) && text[i] is not ('/' or '>'))
            {
                i++;
            }

            return i;
        }

        /// <summary>
        /// Index after the tag's closing '&gt;', or -1 at EOF. Follows the tokenizer's attribute states, so '&gt;' inside a
        /// quoted value doesn't end the tag and <paramref name="selfClosing"/> is set only for a real "/&gt;" (a '/' in an
        /// unquoted attribute value belongs to the value).
        /// </summary>
        private static int TagEnd(ReadOnlySpan<char> text, int start, out bool selfClosing)
        {
            selfClosing = false;
            var i = start;
            var afterSlash = false;                                 // "self-closing start tag" state
            while (i < text.Length)
            {
                var c = text[i];
                if (c == '>')
                {
                    selfClosing = afterSlash;
                    return i + 1;
                }

                if (c == '/')
                {
                    afterSlash = true;
                    i++;
                    continue;
                }

                afterSlash = false;
                if (IsWhitespace(c))
                {
                    i++;
                    continue;
                }

                // Attribute name: its first character is always part of it (even '=' or a quote: "<a =\"x>" ends at the
                // first '>'), then up to whitespace, '/', '>' or '='.
                i++;
                while (i < text.Length && !IsWhitespace(text[i]) && text[i] is not ('/' or '>' or '='))
                {
                    i++;
                }

                while (i < text.Length && IsWhitespace(text[i]))
                {
                    i++;
                }

                if (i >= text.Length || text[i] != '=')
                {
                    continue;                                       // no value
                }

                // Attribute value: optional whitespace, then quoted (may contain '>' and '/') or unquoted (runs to
                // whitespace or '>', and includes '/').
                i++;
                while (i < text.Length && IsWhitespace(text[i]))
                {
                    i++;
                }

                if (i < text.Length && text[i] is '"' or '\'')
                {
                    var close = text[(i + 1)..].IndexOf(text[i]);
                    if (close < 0)
                    {
                        return -1;
                    }

                    i += close + 2;
                }
                else
                {
                    while (i < text.Length && !IsWhitespace(text[i]) && text[i] != '>')
                    {
                        i++;
                    }
                }
            }

            return -1;
        }

        /// <summary>Index after the end tag of a raw-text element, or -1 if it never ends (the rest is text).</summary>
        private static int SkipRawText(ReadOnlySpan<char> text, int start, ReadOnlySpan<char> name)
        {
            var i = start;
            while (true)
            {
                var close = text[i..].IndexOf("</", StringComparison.Ordinal);
                if (close < 0)
                {
                    return -1;
                }

                i += close + 2;
                var after = i + name.Length;
                if (text[i..].StartsWith(name, StringComparison.OrdinalIgnoreCase)
                    && (after >= text.Length || IsWhitespace(text[after]) || text[after] is '/' or '>'))
                {
                    return TagEnd(text, after, out _);
                }
            }
        }

        /// <summary>"&lt;!" was consumed: a comment, or a doctype/CDATA/bogus comment. Index after it, or -1 at EOF.</summary>
        private static int SkipMarkupDeclaration(ReadOnlySpan<char> text, int start)
        {
            var rest = text[start..];
            if (!rest.StartsWith("--", StringComparison.Ordinal))
            {
                // Doctype, "<![CDATA[" (a bogus comment in HTML content; ending it at '>' can only over-count in
                // foreign content), or a bogus comment.
                var gt = rest.IndexOf('>');
                return gt < 0 ? -1 : start + gt + 1;
            }

            var body = rest[2..];
            var bodyStart = start + 2;
            if (body.StartsWith(">", StringComparison.Ordinal))
            {
                return bodyStart + 1;                               // "<!-->"
            }

            if (body.StartsWith("->", StringComparison.Ordinal))
            {
                return bodyStart + 2;                               // "<!--->"
            }

            // The earliest "-->" or "--!>" ends the comment.
            var from = 0;
            while (true)
            {
                var dashes = body[from..].IndexOf("--", StringComparison.Ordinal);
                if (dashes < 0)
                {
                    return -1;
                }

                var at = from + dashes + 2;
                if (at < body.Length && body[at] == '>')
                {
                    return bodyStart + at + 1;
                }

                if (at + 1 < body.Length && body[at] == '!' && body[at + 1] == '>')
                {
                    return bodyStart + at + 2;
                }

                from += dashes + 1;
            }
        }
    }
}
