using System.Collections.Concurrent;
using MdReader.Core.Rendering;

namespace MdReader.Web;

/// <summary>A Markdown file from <c>content/</c>, and what MdReader's own renderer made of it.</summary>
internal sealed record RenderedMarkdown(string Source, string Html, IReadOnlyList<TocEntry> Toc, RenderFeatures Features);

/// <summary>
/// The bodies of the public pages, and the sample documents their editors start with.
/// </summary>
/// <remarks>
/// <para>The prose is written as Markdown and goes through <see cref="IMarkdownRenderer"/> — the same call the
/// render endpoint makes for a visitor's document, with the same sanitizer after it. The cheat sheet is the obvious
/// case for that, since a page about Markdown that was hand-written in HTML would be proving nothing, but it holds
/// for every page: if the renderer breaks, the site says so on every URL rather than only in a test.</para>
/// <para><c>AllowLocalResources</c> is false here for the same reason it is false on the endpoint: rendering must
/// never make the server touch its own file system. Figures are placed by the template instead
/// (<see cref="SiteImages"/>).</para>
/// <para>Files are read and rendered once each, on the first request that needs them, and kept: they do not change
/// while the server runs.</para>
/// </remarks>
internal sealed class SiteContent(IMarkdownRenderer renderer)
{
    /// <summary>Where the content files land in the published output (see the csproj: deliberately not in wwwroot).</summary>
    private const string FolderName = "content";

    private static readonly RenderContext Context = new("/page.md", "/") { AllowLocalResources = false };

    private readonly ConcurrentDictionary<string, RenderedMarkdown> _cache = new(StringComparer.Ordinal);
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, FolderName);

    public RenderedMarkdown Get(string fileName)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        return _cache.GetOrAdd(fileName, Render);
    }

    private RenderedMarkdown Render(string fileName)
    {
        var source = File.ReadAllText(Path.Combine(_root, fileName));
        var result = renderer.Render(source, Context);
        return new RenderedMarkdown(source, result.Html, result.Toc, result.Features);
    }
}
