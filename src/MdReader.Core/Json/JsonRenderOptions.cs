namespace MdReader.Core.Json;

/// <summary>
/// The budgets and display choices of one JSON render (JSON-PLAN, "Limits, because the document is hostile"). The
/// defaults are what a desktop or web render should use; the tests drive them down so that a budget can be proved to
/// work without building a quarter of a million nodes first.
/// </summary>
public sealed record JsonRenderOptions
{
    /// <summary>The defaults, as a shared instance.</summary>
    public static JsonRenderOptions Default { get; } = new();

    /// <summary>
    /// How many levels of the tree are expanded when the document opens; deeper nodes are rendered collapsed, with
    /// their size in the summary. 3 means the root and two levels below it.
    /// </summary>
    public int OpenDepth { get; init; } = 3;

    /// <summary>
    /// The deepest JSON nesting that is rendered as a tree; a document past it is shown as its own text instead.
    /// </summary>
    /// <remarks>
    /// Each JSON level costs three HTML levels (<c>ul</c> &gt; <c>li</c> &gt; <c>details</c>), so 128 JSON levels is
    /// about 390 HTML levels — inside both the sanitizer's ceiling of 512 (<c>RenderLimits.MaxHtmlNesting</c>) and the
    /// depth a browser will build. The parse itself is not recursive, so depth costs no stack either way.
    /// </remarks>
    public int MaxDepth { get; init; } = 128;

    /// <summary>
    /// The largest number of JSON values the tree may contain. Past it the document is shown as its own text with a
    /// line saying why: a quarter of a million rows is already more than anyone will scroll, and every one of them is
    /// several DOM nodes in a page that also has to stay responsive.
    /// </summary>
    public int MaxNodes { get; init; } = 250_000;

    /// <summary>
    /// Characters of a string value or a key shown before the rest is elided (the count of what was left out is shown
    /// after it). Phase 2 adds expanding and copying the whole value.
    /// </summary>
    public int MaxStringLength { get; init; } = 512;

    /// <summary>
    /// Entries the table of contents may hold. A contents panel with ten thousand rows in it is not a contents panel,
    /// and the list travels to the page in one message.
    /// </summary>
    public int MaxTocEntries { get; init; } = 500;

    /// <summary>
    /// Characters of HTML a document earns per character of its own length. A short document can nest and repeat its
    /// way into a great deal of markup (<c>[0,0,0,…]</c> is two characters of source per row), so the node ceiling
    /// alone does not bound the output; this does. The densest legitimate shapes run at about 25× and ordinary
    /// documents at well under 10×, so 48 leaves room without leaving the door open.
    /// </summary>
    public int HtmlBudgetPerCharacter { get; init; } = 48;

    /// <summary>Characters every document earns whatever its size, so that a tiny one is not penalised by rounding.</summary>
    public long HtmlBudgetFloor { get; init; } = 262_144;

    /// <summary>Characters of HTML the tree may produce for a document of <paramref name="sourceLength"/> characters.</summary>
    public long HtmlBudget(int sourceLength) => ((long)HtmlBudgetPerCharacter * sourceLength) + HtmlBudgetFloor;
}
