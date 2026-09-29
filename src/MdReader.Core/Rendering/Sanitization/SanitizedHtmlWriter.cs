using MdReader.Core.Protocol;
using System.Buffers;
using System.Text;
using AngleSharp.Dom;
using MdReader.Core.Paths;

namespace MdReader.Core.Rendering.Sanitization;

/// <summary>
/// Post-processes and serializes the sanitized body in ONE pass (ARCHITECTURE §4.2, §8.2):
/// <list type="bullet">
/// <item>per-element attribute rules (<see cref="SanitizerPolicy.IsAllowedAttribute"/>), <c>ol type</c> values;</item>
/// <item><c>id</c> (and id references) through <see cref="ContentIdPolicy"/>; <c>class</c> tokens starting <c>mdr-</c> removed;</item>
/// <item><c>input</c> kept only as a disabled checkbox; <c>source</c> kept only inside <c>picture</c>;</item>
/// <item>fail-closed: anything that is not an allowed HTML-namespace element, text or element is not written.</item>
/// </list>
/// Serialization follows the HTML fragment serialization algorithm exactly as AngleSharp's <c>ToHtml()</c> does
/// (tests assert string equality); AngleSharp's own serializer is ~100x slower on large documents.
/// The traversal is iterative (explicit stack, index-based child access), so deeply nested DOMs can't overflow the stack.
/// </summary>
internal static class SanitizedHtmlWriter
{
    private const string HtmlNamespace = "http://www.w3.org/1999/xhtml";
    private const int CancellationCheckInterval = 4096;

    private static readonly SearchValues<char> TextSpecials = SearchValues.Create("&<>\u00A0");
    private static readonly SearchValues<char> AttributeSpecials = SearchValues.Create("&\"\u00A0");
    private static readonly SearchValues<char> AsciiWhitespace = SearchValues.Create(" \t\n\f\r");

    /// Post-processes (per-element attribute rules, ids, classes, inputs) and serializes the sanitized body
    /// in ONE recursive pass. Records the start offset of every top-level node.
    /// <remarks>
    /// Offsets are those of the top-level nodes the output parses back into: nodes that produce no output get none, and
    /// adjacent top-level text nodes (left behind when an element between them was removed) count as one node.
    /// </remarks>
    public static SanitizedHtml Write(IElement body, CancellationToken cancellationToken) =>
        Write(body, long.MaxValue, null, allowLocalLinks: true, cancellationToken);

    /// <summary>
    /// As <see cref="Write(IElement, CancellationToken)"/>, with an output budget (<see cref="RenderLimits.OutputBudget"/>)
    /// and the render's task-line token: a checkbox whose <c>data-line</c> starts with
    /// <paramref name="taskLinePrefix"/><c>:</c> keeps the line number and stays enabled; every other <c>input</c> is
    /// rebuilt as a disabled checkbox, so document HTML can't forge the attribute (§4.1).
    /// </summary>
    /// <param name="allowLocalLinks">
    /// False for a document fetched from the web (<see cref="RenderContext.BaseUrl"/>): the second, independent href
    /// check below then refuses <c>file:</c>, <c>C:\…</c> and <c>\\server\…</c> as well, so a bug in the first layer
    /// still can't let a remote author offer a link to this machine.
    /// </param>
    /// <exception cref="RenderLimitExceededException">The output would be longer than <paramref name="maxOutputLength"/>.</exception>
    internal static SanitizedHtml Write(IElement body, long maxOutputLength, string? taskLinePrefix, bool allowLocalLinks,
                                        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var writer = new Writer(maxOutputLength, taskLinePrefix, allowLocalLinks, cancellationToken);
        var offsets = new List<int>();
        var children = body.ChildNodes;
        var previousWasText = false;
        for (var i = 0; i < children.Length; i++)
        {
            var child = children[i];
            var start = writer.Output.Length;
            writer.WriteSubtree(child);
            if (writer.Output.Length == start)
            {
                continue;
            }

            var isText = child is IText;
            if (!(isText && previousWasText))
            {
                offsets.Add(start);
            }

            previousWasText = isText;
        }

        return new SanitizedHtml(writer.Output.ToString(), offsets);
    }

    /// <summary>What the writer does with an element.</summary>
    private enum ElementAction
    {
        /// <summary>Neither the element nor its subtree is written (HtmlSanitizer already unwrapped what it had to).</summary>
        Drop,
        Write,
        WriteCheckbox,
    }

    private static ElementAction Classify(IElement element)
    {
        if (!string.Equals(element.NamespaceUri, HtmlNamespace, StringComparison.Ordinal))
        {
            return ElementAction.Drop;
        }

        var name = element.LocalName;
        if (!SanitizerPolicy.AllowedTags.Contains(name))
        {
            return ElementAction.Drop;
        }

        switch (name)
        {
            case "input":
                return string.Equals(element.GetAttribute("type"), "checkbox", StringComparison.OrdinalIgnoreCase)
                    ? ElementAction.WriteCheckbox
                    : ElementAction.Drop;
            case "source":
                return element.ParentElement is { LocalName: "picture" } parent
                       && string.Equals(parent.NamespaceUri, HtmlNamespace, StringComparison.Ordinal)
                    ? ElementAction.Write
                    : ElementAction.Drop;
            default:
                return ElementAction.Write;
        }
    }

    /// <summary>True if the node produces no output at all (so it doesn't count as the first child of a <c>pre</c>).</summary>
    private static bool IsSkipped(INode node) => node switch
    {
        IElement element => Classify(element) == ElementAction.Drop,
        IText => false,
        _ => true,
    };

    private static void AppendEscaped(StringBuilder output, ReadOnlySpan<char> value, SearchValues<char> specials)
    {
        while (true)
        {
            var index = value.IndexOfAny(specials);
            if (index < 0)
            {
                output.Append(value);
                return;
            }

            output.Append(value[..index]);
            output.Append(value[index] switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                _ => "&nbsp;",
            });
            value = value[(index + 1)..];
        }
    }

    /// <summary>
    /// Defense in depth (the URL rules proper run in HtmlSanitizer's <c>FilterUrl</c> hook). An <c>href</c> has to pass
    /// two checks that do not share an implementation: <see cref="IsSafeHrefScheme"/>, which decides for itself which
    /// scheme the browser will see, and <see cref="ResourceUrlRewriter.FilterHref"/>, which must have returned the value
    /// unchanged. Either one alone would stop a <c>javascript:</c> href; a bug in one is caught by the other. Every
    /// image URL must be <c>https:</c> or <c>data:image/</c>, so no path through the sanitizer can emit another scheme.
    /// </summary>
    internal static bool IsSafeUrlAttribute(string attributeName, string value, bool allowLocalLinks)
    {
        switch (attributeName)
        {
            case "href":
                return IsSafeHrefScheme(value, allowLocalLinks)
                       && string.Equals(ResourceUrlRewriter.FilterHref(value, isRemote: !allowLocalLinks), value,
                                        StringComparison.Ordinal);
            case "src":
                return IsSafeImageUrl(value);
            case "srcset":
                foreach (var range in value.AsSpan().Split(','))
                {
                    var candidate = value.AsSpan(range).TrimStart(" \t\n\f\r");
                    if (candidate.IsEmpty)
                    {
                        continue;
                    }

                    var urlEnd = candidate.IndexOfAny(AsciiWhitespace);
                    if (!IsSafeImageUrl(urlEnd < 0 ? candidate : candidate[..urlEnd]))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return false;
        }

        // https, data:image/, and the scheme this process serves its own document host over. That last one is https
        // on Windows and mdreader: on macOS, where the WKWebView backend registers its own scheme — hard-coding
        // https here dropped every local image on that platform, and the resolver that would have served them was
        // never asked for one.
        static bool IsSafeImageUrl(ReadOnlySpan<char> url) =>
            url.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
            || IsDocumentHostScheme(url);

        static bool IsDocumentHostScheme(ReadOnlySpan<char> url)
        {
            var scheme = ProtocolConstants.WebScheme;
            return url.Length > scheme.Length
                   && url[scheme.Length] == ':'
                   && url[..scheme.Length].Equals(scheme, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// The writer's own answer to "what scheme will the browser see here", written without looking at
    /// <see cref="ResourceUrlRewriter"/> at all — that is what makes it a second opinion rather than an echo.
    /// </summary>
    /// <remarks>
    /// <para>The scheme is read off the string the way the WHATWG URL parser does: leading C0 controls and spaces are
    /// skipped and TAB/CR/LF are ignored everywhere, because the browser removes them before parsing. That is what
    /// stops <c>jav&amp;#x09;ascript:</c>, which is an ordinary-looking relative reference until those characters go.</para>
    /// <para>Allowed: no scheme at all (a fragment, a relative or root-relative path, a protocol-relative URL),
    /// <c>http</c>, <c>https</c> and <c>mailto</c>; and, only for a document on this machine, <c>file</c> and a
    /// drive letter. The host policy is shared on purpose — there is one definition of "the app's own hosts" — but the
    /// decision about which shapes may appear at all is made here.</para>
    /// </remarks>
    internal static bool IsSafeHrefScheme(string value, bool allowLocalLinks)
    {
        var url = CleanForSchemeCheck(value);
        if (url.Length == 0)
        {
            return false;
        }

        if (!char.IsAsciiLetter(url[0]))
        {
            return true;   // '#', '/', '.', '?' … : no scheme, so nothing but a path
        }

        var end = 1;
        while (end < url.Length && (char.IsAsciiLetterOrDigit(url[end]) || url[end] is '+' or '-' or '.'))
        {
            end++;
        }

        if (end >= url.Length || url[end] != ':')
        {
            return true;   // "readme.md", "docs/a.md#x" … : still no scheme
        }

        var scheme = url.AsSpan(0, end);
        if (scheme.Length == 1)
        {
            // A drive letter, which is a path and not a scheme - and only ever on this machine. "C:x" (drive
            // relative) is not an absolute path, so it is refused along with everything else.
            return allowLocalLinks && url.Length > 2 && char.IsAsciiLetter(url[0])
                   && (url[2] is '/' or '\\' || url.AsSpan(2).StartsWith("%5C", StringComparison.OrdinalIgnoreCase));
        }

        if (scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
            || scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
        {
            // One definition of "our own hosts", deliberately shared.
            return !(Uri.TryCreate(url, UriKind.Absolute, out var absolute) && WebUrlPolicy.IsReservedHost(absolute));
        }

        if (scheme.Equals("mailto", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return allowLocalLinks && scheme.Equals("file", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What the browser's URL parser throws away before it looks for a scheme.</summary>
    private static string CleanForSchemeCheck(string value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && value[start] <= ' ')
        {
            start++;
        }

        while (end > start && value[end - 1] <= ' ')
        {
            end--;
        }

        var span = value.AsSpan(start, end - start);
        if (span.IndexOfAny('\t', '\n', '\r') < 0)
        {
            return span.Length == value.Length ? value : span.ToString();
        }

        var buffer = new char[span.Length];
        var length = 0;
        foreach (var c in span)
        {
            if (c is not ('\t' or '\n' or '\r'))
            {
                buffer[length++] = c;
            }
        }

        return new string(buffer, 0, length);
    }

    /// <summary>Removes class tokens starting with <c>mdr-</c>; null if nothing remains.</summary>
    internal static string? FilterClass(string value)
    {
        if (value.AsSpan().IndexOfAnyExcept(AsciiWhitespace) < 0)
        {
            return null;
        }

        if (value.IndexOf(ContentIdPolicy.ReservedPrefix, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return value;
        }

        var kept = new List<string>();
        var removed = false;
        foreach (var token in SplitTokens(value))
        {
            if (token.StartsWith(ContentIdPolicy.ReservedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                removed = true;
            }
            else
            {
                kept.Add(token);
            }
        }

        if (!removed)
        {
            return value;
        }

        return kept.Count == 0 ? null : string.Join(' ', kept);
    }

    /// <summary>Maps every id in a space-separated id-reference list through <see cref="ContentIdPolicy"/>.</summary>
    internal static string MapIdReferences(string value)
    {
        if (value.IndexOf(ContentIdPolicy.ReservedPrefix, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return value;
        }

        var tokens = SplitTokens(value);
        var changed = false;
        for (var i = 0; i < tokens.Count; i++)
        {
            var mapped = ContentIdPolicy.ToContentId(tokens[i])!;
            changed |= !ReferenceEquals(mapped, tokens[i]);
            tokens[i] = mapped;
        }

        return changed ? string.Join(' ', tokens) : value;
    }

    private static List<string> SplitTokens(string value)
    {
        var tokens = new List<string>();
        var span = value.AsSpan();
        while (true)
        {
            var start = span.IndexOfAnyExcept(AsciiWhitespace);
            if (start < 0)
            {
                return tokens;
            }

            span = span[start..];
            var end = span.IndexOfAny(AsciiWhitespace);
            if (end < 0)
            {
                tokens.Add(span.ToString());
                return tokens;
            }

            tokens.Add(span[..end].ToString());
            span = span[end..];
        }
    }

    /// <summary>The per-call serializer state.</summary>
    private sealed class Writer(long maxOutputLength, string? taskLinePrefix, bool allowLocalLinks,
                                CancellationToken cancellationToken)
    {
        /// <summary>The digits of a pipeline-issued <c>data-line</c>, or null (author HTML, or no task list).</summary>
        private ReadOnlySpan<char> TaskLine(IElement element)
        {
            if (taskLinePrefix is null || element.GetAttribute("data-line") is not { } value)
            {
                return default;
            }

            if (value.Length <= taskLinePrefix.Length + 1
                || !value.StartsWith(taskLinePrefix, StringComparison.Ordinal)
                || value[taskLinePrefix.Length] != ':')
            {
                return default;
            }

            var digits = value.AsSpan(taskLinePrefix.Length + 1);
            if (digits.Length > 9)
            {
                return default;
            }

            foreach (var c in digits)
            {
                if (!char.IsAsciiDigit(c))
                {
                    return default;
                }
            }

            return digits;
        }

        private Frame[] _stack = new Frame[32];
        private int _depth;
        private int _nodeCount;

        public StringBuilder Output { get; } = new();

        /// <summary>Writes <paramref name="root"/> and its subtree.</summary>
        public void WriteSubtree(INode root)
        {
            WriteNode(root);
            while (_depth > 0)
            {
                ref var top = ref _stack[_depth - 1];
                if (top.Index < top.Children.Length)
                {
                    var child = top.Children[top.Index];
                    top.Index++;
                    WriteNode(child);   // may push (and thereby reallocate _stack): don't touch `top` afterwards
                }
                else
                {
                    Output.Append("</").Append(top.Element.LocalName).Append('>');
                    top = default;
                    _depth--;
                }
            }
        }

        private void WriteNode(INode node)
        {
            if (++_nodeCount % CancellationCheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            switch (node)
            {
                case IText text:
                    AppendEscaped(Output, text.Data, TextSpecials);
                    break;
                case IElement element:
                    WriteElement(element);
                    break;
                default:
                    break;          // comments, doctypes, processing instructions: never written
            }

            // Checked per node: one node adds at most one text or one start tag, so the overshoot is bounded.
            if (Output.Length > maxOutputLength)
            {
                throw new RenderLimitExceededException("The sanitized HTML would be too large (cloned-element amplification).");
            }
        }

        private void WriteElement(IElement element)
        {
            switch (Classify(element))
            {
                case ElementAction.Drop:
                    return;
                case ElementAction.WriteCheckbox:
                    // Rebuilt from scratch: nothing but the checked state and a pipeline-issued line number survives.
                    Output.Append("<input type=\"checkbox\"");
                    var taskLine = TaskLine(element);
                    if (taskLine.IsEmpty)
                    {
                        Output.Append(" disabled=\"\"");
                    }
                    else
                    {
                        Output.Append(" data-line=\"").Append(taskLine).Append('"');
                    }

                    if (element.HasAttribute("checked"))
                    {
                        Output.Append(" checked=\"\"");
                    }

                    Output.Append('>');
                    return;
            }

            var name = element.LocalName;
            Output.Append('<').Append(name);
            WriteAttributes(element, name);
            Output.Append('>');

            if ((element.Flags & NodeFlags.SelfClosing) != 0)
            {
                return;             // void element: no children, no end tag
            }

            var children = element.ChildNodes;
            if ((element.Flags & NodeFlags.LineTolerance) != 0 && StartsWithNewline(children))
            {
                // pre/listing/textarea: the parser drops one leading newline, so emit an extra one.
                Output.Append('\n');
            }

            if (children.Length == 0)
            {
                Output.Append("</").Append(name).Append('>');
                return;
            }

            if (_depth == _stack.Length)
            {
                Array.Resize(ref _stack, _stack.Length * 2);
            }

            _stack[_depth++] = new Frame(element, children);
        }

        private void WriteAttributes(IElement element, string elementName)
        {
            var attributes = element.Attributes;
            for (var i = 0; i < attributes.Length; i++)
            {
                var attribute = attributes[i];
                if (attribute is null || !string.IsNullOrEmpty(attribute.NamespaceUri))
                {
                    continue;
                }

                var name = attribute.Name;
                if (!SanitizerPolicy.IsAllowedAttribute(elementName, name))
                {
                    continue;
                }

                string? value = attribute.Value;
                switch (name)
                {
                    case "id":
                        value = ContentIdPolicy.ToContentId(value);
                        break;
                    case "class":
                        value = FilterClass(value);
                        break;
                    case "aria-describedby":
                    case "aria-labelledby":
                    case "headers":
                        value = MapIdReferences(value);
                        break;
                    case "type" when elementName == "ol":
                        value = SanitizerPolicy.OrderedListTypes.Contains(value) ? value : null;
                        break;
                    case "href":
                    case "src":
                    case "srcset":
                        value = IsSafeUrlAttribute(name, value, allowLocalLinks) ? value : null;
                        break;
                }

                if (value is null)
                {
                    continue;
                }

                Output.Append(' ').Append(name).Append("=\"");
                AppendEscaped(Output, value, AttributeSpecials);
                Output.Append('"');
            }
        }

        private static bool StartsWithNewline(INodeList children)
        {
            for (var i = 0; i < children.Length; i++)
            {
                var child = children[i];
                if (IsSkipped(child))
                {
                    continue;
                }

                return child is IText text && text.Data.StartsWith('\n');
            }

            return false;
        }
    }

    private struct Frame(IElement element, INodeList children)
    {
        public readonly IElement Element = element;
        public readonly INodeList Children = children;
        public int Index;
    }
}
