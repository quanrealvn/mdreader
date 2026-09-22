using System.Buffers;
using System.Text;
using AngleSharp.Dom;

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
    public static SanitizedHtml Write(IElement body, CancellationToken cancellationToken) => Write(body, long.MaxValue, cancellationToken);

    /// <summary>As <see cref="Write(IElement, CancellationToken)"/>, with an output budget (<see cref="RenderLimits.OutputBudget"/>).</summary>
    /// <exception cref="RenderLimitExceededException">The output would be longer than <paramref name="maxOutputLength"/>.</exception>
    internal static SanitizedHtml Write(IElement body, long maxOutputLength, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(body);
        var writer = new Writer(maxOutputLength, cancellationToken);
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
    /// Defense in depth (the URL rules proper run in HtmlSanitizer's <c>FilterUrl</c> hook): an <c>href</c> must still pass
    /// <see cref="ResourceUrlRewriter.FilterHref"/> unchanged, and every image URL must be <c>https:</c> or
    /// <c>data:image/</c>. Anything else is dropped, so no path through the sanitizer can emit another scheme.
    /// </summary>
    internal static bool IsSafeUrlAttribute(string attributeName, string value)
    {
        switch (attributeName)
        {
            case "href":
                return string.Equals(ResourceUrlRewriter.FilterHref(value), value, StringComparison.Ordinal);
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

        static bool IsSafeImageUrl(ReadOnlySpan<char> url) =>
            url.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);
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
    private sealed class Writer(long maxOutputLength, CancellationToken cancellationToken)
    {
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
                    // Rebuilt from scratch: nothing but the checked state survives.
                    Output.Append("<input type=\"checkbox\" disabled=\"\"");
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
                        value = IsSafeUrlAttribute(name, value) ? value : null;
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
