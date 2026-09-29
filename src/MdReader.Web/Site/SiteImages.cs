namespace MdReader.Web;

/// <summary>A picture the pages use, with the size it is drawn at so the layout never moves once it loads.</summary>
internal sealed record SiteImage(string Light, string Dark, int Width, int Height, string Alt, string Caption);

/// <summary>
/// The pictures on the site. Figures are placed by the page template rather than written into the Markdown bodies:
/// the renderer drops every local image reference by design (<c>AllowLocalResources = false</c>, ARCHITECTURE §15),
/// so an <c>![…](…)</c> in a content file would silently disappear. A body asks for one with a paragraph of its own
/// containing <c>{{figure:name}}</c>, and <see cref="Expand"/> swaps it for the markup.
/// </summary>
internal static class SiteImages
{
    /// <summary>The Open Graph and Twitter card picture. 1200×630 is the size both of them crop to.</summary>
    public static readonly string OpenGraph = SiteAssets.Image("og-cover.png");

    public const int OpenGraphWidth = 1200;
    public const int OpenGraphHeight = 630;

    public const string OpenGraphAlt =
        "MdReader: a rendered Markdown document beside its contents panel.";

    /// <summary>The screenshot in the about page's prose, and the one the structured data points at.</summary>
    public static readonly string Reader = SiteAssets.Image("reader-light.jpg");

    private const string Marker = "{{figure:";

    private static readonly Dictionary<string, SiteImage> Figures = new(StringComparer.Ordinal)
    {
        ["reader"] = new SiteImage(
            Reader,
            SiteAssets.Image("reader-dark.jpg"),
            960,
            624,
            "A rendered Markdown document with coloured headings, inline code and a highlighted phrase, "
            + "beside a contents panel listing its sections.",
            "The reader with a document open and its contents panel beside it."),
        ["windows"] = new SiteImage(
            SiteAssets.Image("windows-light.jpg"),
            SiteAssets.Image("windows-dark.jpg"),
            900,
            675,
            "The MdReader window on Windows, with two file tabs, a toolbar, a contents panel and a rendered "
            + "document.",
            "Two files open in tabs. The contents panel on the left is built from the document's headings."),
    };

    /// <summary>
    /// Replaces every figure marker in rendered body HTML. Throws when a marker names a picture that does not exist,
    /// or when one survives: a silently dropped figure is the failure mode worth making loud, and it happens at
    /// startup where a test sees it.
    /// </summary>
    public static string Expand(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        if (!html.Contains(Marker, StringComparison.Ordinal))
        {
            return html;
        }

        foreach (var (name, image) in Figures)
        {
            html = html.Replace("<p>" + Marker + name + "}}</p>", Figure(image), StringComparison.Ordinal);
        }

        var leftover = html.IndexOf(Marker, StringComparison.Ordinal);
        if (leftover >= 0)
        {
            var end = html.IndexOf("}}", leftover, StringComparison.Ordinal);
            var token = end > leftover ? html[leftover..(end + 2)] : html[leftover..];
            throw new InvalidOperationException($"Unknown figure marker in site content: {token}");
        }

        return html;
    }

    /// <summary>Every file <see cref="Figures"/> and the card picture name, so a test can see that each one is served.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        OpenGraph,
        .. Figures.Values.SelectMany(image => new[] { image.Light, image.Dark }),
    ];

    private static string Figure(SiteImage image) =>
        $"""
         <figure class="mdr-site-figure">
         <picture>
         <source srcset="{image.Dark}" media="(prefers-color-scheme: dark)">
         <img src="{image.Light}" width="{image.Width}" height="{image.Height}" loading="lazy" decoding="async"
          alt="{HtmlText.Escape(image.Alt)}">
         </picture>
         <figcaption>{HtmlText.Escape(image.Caption)}</figcaption>
         </figure>
         """;
}
