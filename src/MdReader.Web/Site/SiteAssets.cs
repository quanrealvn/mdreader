namespace MdReader.Web;

/// <summary>
/// Where the public pages' own stylesheet, scripts and pictures live.
/// </summary>
/// <remarks>
/// <para>They sit under one prefix of their own, <c>/site/</c>, and they come from
/// <c>src/MdReader.Web/site-assets</c> (see the csproj), not from <c>web/</c> or <c>webapp/</c>. That is the whole
/// point: the reader's files are read by these pages and never written to. A stylesheet added to <c>webapp/css</c>
/// would have been a modification of the app's folder even though the app never loaded it, and the rule here is that
/// there are none.</para>
/// <para>The prefix is a single segment so that it cannot collide with a page path — every page is one segment too,
/// and <c>SiteSeoTests.SiteAssetPrefixIsNotAPagePath</c> says so.</para>
/// </remarks>
internal static class SiteAssets
{
    /// <summary>The URL prefix, with its leading and trailing slash.</summary>
    public const string Prefix = "/site/";

    public const string Stylesheet = Prefix + "site.css";

    /// <summary>Classic and synchronous, in the head: it sets the theme and <c>data-js</c> before the first paint.</summary>
    public const string InitScript = Prefix + "site-init.js";

    /// <summary>The module that makes the worked example on a tool page editable.</summary>
    public const string Script = Prefix + "site.js";

    /// <summary>The absolute path of a picture under <c>site-assets/img</c>.</summary>
    public static string Image(string fileName) => Prefix + "img/" + fileName;
}
