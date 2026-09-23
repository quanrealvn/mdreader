using System.Collections.Frozen;
using AngleSharp.Css.Dom;
using Ganss.Xss;

namespace MdReader.Core.Rendering.Sanitization;

/// <summary>
/// Immutable allow-lists (ARCHITECTURE §8.2). HtmlSanitizer sees the union of all attributes; the per-element rules are
/// enforced by <see cref="SanitizedHtmlWriter"/>.
/// </summary>
internal static class SanitizerPolicy
{
    /// <summary>Tags that survive sanitization.</summary>
    public static readonly FrozenSet<string> AllowedTags = Set(
        "a", "abbr", "b", "bdi", "bdo", "blockquote", "br", "caption", "cite", "code", "col", "colgroup", "dd", "del",
        "details", "dfn", "div", "dl", "dt", "em", "figcaption", "figure", "h1", "h2", "h3", "h4", "h5", "h6", "hr", "i",
        "img", "input", "ins", "kbd", "li", "mark", "ol", "p", "picture", "pre", "q", "rp", "rt", "ruby", "s", "samp",
        "small", "source", "span", "strike", "strong", "sub", "summary", "sup", "table", "tbody", "td", "tfoot", "th",
        "thead", "time", "tr", "tt", "u", "ul", "var", "wbr");

    /// <summary>
    /// Disallowed tags whose children are removed together with the tag (so no script/CSS/fallback text leaks out).
    /// Every other disallowed tag is unwrapped: the tag goes, its children stay.
    /// </summary>
    public static readonly FrozenSet<string> DropSubtreeTags = Set(
        "script", "style", "template", "noscript", "iframe", "frame", "frameset", "object", "embed", "applet", "svg",
        "math", "canvas", "audio", "video", "textarea", "select", "option", "optgroup", "button", "form", "title", "head",
        "xmp", "noembed", "noframes", "plaintext", "meta", "link", "base", "dialog", "portal");

    /// <summary>Attributes allowed on every allowed element (except <c>input</c>, which is rebuilt).</summary>
    public static readonly FrozenSet<string> GlobalAttributes = Set(
        "id", "class", "title", "lang", "dir", "aria-label", "aria-hidden", "aria-describedby", "aria-labelledby");

    /// <summary>Per-element attributes, in addition to <see cref="GlobalAttributes"/>.</summary>
    public static readonly FrozenDictionary<string, FrozenSet<string>> ElementAttributes = BuildElementAttributes();

    /// <summary>Attributes whose value is a space-separated list of element ids (mapped through <see cref="ContentIdPolicy"/>).</summary>
    public static readonly FrozenSet<string> IdReferenceAttributes = Set("aria-describedby", "aria-labelledby", "headers");

    /// <summary>Valid values of <c>ol type</c> (case-sensitive: <c>a</c> and <c>A</c> differ).</summary>
    public static readonly FrozenSet<string> OrderedListTypes = FrozenSet.ToFrozenSet(["1", "a", "A", "i", "I"], StringComparer.Ordinal);

    /// <summary>Attributes of <c>input</c> that the writer needs to see (the element itself is rebuilt).</summary>
    /// <remarks>
    /// <c>data-line</c> survives HtmlSanitizer on every element, but only the writer's checkbox branch ever emits it,
    /// and only when its value carries the render's task-line token (§4.1): <see cref="IsAllowedAttribute"/> keeps it
    /// off everything else.
    /// </remarks>
    private static readonly string[] InputAttributes = ["type", "checked", "data-line"];

    /// <summary>The union HtmlSanitizer is configured with.</summary>
    public static readonly FrozenSet<string> SanitizerAttributes = GlobalAttributes
        .Concat(ElementAttributes.Values.SelectMany(static set => set))
        .Concat(InputAttributes)
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static readonly FrozenSet<string> AllowedSchemes = Set("http", "https", "mailto");

    public static readonly FrozenSet<string> UriAttributes = Set("href", "src");

    public static readonly FrozenSet<string> UriListAttributes = Set("srcset");

    /// <summary>
    /// Shared, never-mutated options. HtmlSanitizer copies the sets on construction, and the frozen sets throw if anything
    /// ever tries to mutate them.
    /// </summary>
    public static readonly HtmlSanitizerOptions Options = new()
    {
        AllowedTags = AllowedTags,
        AllowedAttributes = SanitizerAttributes,
        AllowedCssClasses = FrozenSet<string>.Empty,
        AllowedCssProperties = FrozenSet<string>.Empty,
        AllowedAtRules = FrozenSet<CssRuleType>.Empty,
        AllowedSchemes = AllowedSchemes,
        UriAttributes = UriAttributes,
        UriListAttributes = UriListAttributes,
        AllowCssCustomProperties = false,
        AllowDataAttributes = false,
    };

    /// <summary>True if <paramref name="attributeName"/> may appear on the (lower-case, HTML-namespace) element.</summary>
    public static bool IsAllowedAttribute(string elementName, string attributeName) =>
        GlobalAttributes.Contains(attributeName)
        || (ElementAttributes.TryGetValue(elementName, out var allowed) && allowed.Contains(attributeName));

    private static FrozenDictionary<string, FrozenSet<string>> BuildElementAttributes()
    {
        var map = new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal)
        {
            ["a"] = Set("href"),
            ["img"] = Set("src", "srcset", "sizes", "alt", "width", "height", "align"),
            ["source"] = Set("srcset", "sizes", "media", "type"),
            ["ol"] = Set("start", "reversed", "type"),
            ["li"] = Set("value"),
            ["details"] = Set("open"),
        };

        var cell = Set("colspan", "rowspan", "align", "valign", "scope", "headers", "abbr");
        map["td"] = cell;
        map["th"] = cell;

        var column = Set("span", "width", "align", "valign");
        map["col"] = column;
        map["colgroup"] = column;

        var alignAndWidth = Set("align", "width");
        map["table"] = alignAndWidth;
        map["hr"] = alignAndWidth;

        var alignOnly = Set("align");
        foreach (var name in new[] { "p", "div", "h1", "h2", "h3", "h4", "h5", "h6" })
        {
            map[name] = alignOnly;
        }

        var datetime = Set("datetime");
        map["time"] = datetime;
        map["del"] = datetime;
        map["ins"] = datetime;

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static FrozenSet<string> Set(params string[] values) => values.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
}
