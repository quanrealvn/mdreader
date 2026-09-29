using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MdReader.Web;

/// <summary>
/// Builds the service worker the browser is served (ARCHITECTURE §15): <c>webapp/sw.js</c> with its two placeholders
/// filled in from the files that are actually on disk.
/// </summary>
/// <remarks>
/// <para>The precache list is not written by hand anywhere. It is the reader's own files minus the third-party vendor
/// libraries — six megabytes that are imported only when a document needs math, diagrams or highlighting, and that
/// would make every install heavy and every missing file a failed install. Deriving it means a new CSS or JS file is
/// in the shell the moment it exists, and a list entry can't name a file that isn't served.</para>
/// <para>The cache version is a hash of the worker template and of <em>every file the reader can load</em>,
/// vendor libraries included — they are not precached, but a page still loads them, so a deploy that changes one
/// has to be an update the browser notices. The bytes of <c>/sw.js</c> therefore change if and only if something a
/// page can load changed, which is exactly when the update check should find something. A deploy that changes
/// nothing leaves every visitor on the cache they already have.</para>
/// <para>Neither list contains the public pages' own assets under <c>site/</c> (ARCHITECTURE §15.1). The reader
/// loads none of them, so a change to the marketing pages is not a reason to retire every reader's cache, and their
/// pictures are not the reader's to carry offline.</para>
/// <para>Built once, on the first request, and kept: the files under <c>wwwroot</c> don't change while the server
/// runs.</para>
/// </remarks>
internal sealed class ServiceWorkerScript
{
    /// <summary>Where the worker is served. It must stay at the root: a worker's default scope is its own folder.</summary>
    internal const string RequestPath = "/sw.js";

    internal const string VersionPlaceholder = "__MDR_CACHE_VERSION__";
    internal const string AssetsPlaceholder = "__MDR_ASSETS__";

    /// <summary>Third-party libraries: lazily imported by the reader, far too big to precache — but it does load
    /// them, so they still count towards the version (see <see cref="Build"/>).</summary>
    internal const string VendorPrefix = "vendor/";

    /// <summary>
    /// The public pages' own stylesheet, scripts and pictures (<see cref="SiteAssets.Prefix"/>, §15.1). The reader
    /// loads none of them, so they are neither precached nor part of its version.
    /// </summary>
    internal static readonly string SitePrefix = SiteAssets.Prefix.TrimStart('/');

    /// <summary>The template's path in the published output (see the csproj: it is deliberately not in wwwroot).</summary>
    private const string TemplateRelativePath = "ServiceWorker/sw.js";

    private readonly Lazy<ServiceWorkerContent> _content;

    public ServiceWorkerScript(IWebHostEnvironment environment, ILogger<ServiceWorkerScript> logger)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _content = new Lazy<ServiceWorkerContent>(() =>
        {
            var content = Build(Path.Combine(AppContext.BaseDirectory, TemplateRelativePath), environment.WebRootPath);
            logger.LogInformation("Service worker {Version} covers {Count} shell files.", content.Version, content.Assets.Count);
            return content;
        });
    }

    public ServiceWorkerContent Content => _content.Value;

    /// <summary>The finished script, its cache version and the URLs it precaches.</summary>
    internal sealed record ServiceWorkerContent(string Script, string Version, IReadOnlyList<string> Assets);

    /// <summary>
    /// Reads the template and the shell files and produces the script. Internal and static so a test can run it over
    /// a directory of its own and watch the version change with the content.
    /// </summary>
    internal static ServiceWorkerContent Build(string templatePath, string webRoot)
    {
        var template = File.ReadAllText(templatePath);
        var readerFiles = ReaderFiles(webRoot);
        var shell = readerFiles.Where(IsShellFile).ToList();

        // The version covers every file the READER can load, not only the precached ones. A vendor library is not in
        // the shell, but the reader does import it, so a deploy that changes one has to be an update the browser
        // notices - otherwise /sw.js is byte-identical, no update is found, and open pages keep old app modules while
        // pulling the new library off the network. The public pages' assets are excluded at the other end: the reader
        // never loads them, so a change there is not a reason to retire every reader's cache. The template's own bytes
        // are in here too, so a change to the worker's logic retires the old cache rather than reusing it under a name
        // that no longer describes it.
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AddToHash(digest, TemplateRelativePath, File.ReadAllBytes(templatePath));
        foreach (var file in readerFiles)
        {
            AddToHash(digest, file, File.ReadAllBytes(Path.Combine(webRoot, file.Replace('/', Path.DirectorySeparatorChar))));
        }

        var version = Convert.ToHexStringLower(digest.GetHashAndReset())[..16];

        // "./" is the reader itself: a navigation to "/" is answered with it, and no other navigation is touched.
        var assets = new List<string>(shell.Count + 1) { "./" };
        assets.AddRange(shell.Select(file => "./" + file));

        var script = template
            .Replace(VersionPlaceholder, version, StringComparison.Ordinal)
            .Replace(AssetsPlaceholder, JsonSerializer.Serialize(assets, WebJsonContext.Default.ListString),
                     StringComparison.Ordinal);

        return new ServiceWorkerContent(script, version, assets);
    }

    /// <summary>
    /// Every served file the reader can load, as forward-slash relative paths, ordered so the version is the same on
    /// any machine and any file system. The public pages' assets under <c>site/</c> are not among them.
    /// </summary>
    internal static IReadOnlyList<string> ReaderFiles(string webRoot)
    {
        return [.. Directory
            .EnumerateFiles(webRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(webRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(relative => !relative.StartsWith(SitePrefix, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>Everything the worker precaches: the reader's files minus the third-party libraries.</summary>
    internal static IReadOnlyList<string> ShellFiles(string webRoot) => [.. ReaderFiles(webRoot).Where(IsShellFile)];

    private static bool IsShellFile(string relativePath) =>
        !relativePath.StartsWith(VendorPrefix, StringComparison.OrdinalIgnoreCase);

    private static void AddToHash(IncrementalHash digest, string relativePath, byte[] bytes)
    {
        // The path goes into the hash too: moving a file without changing its bytes is still a change.
        digest.AppendData(Encoding.UTF8.GetBytes(relativePath));
        digest.AppendData([0]);
        digest.AppendData(bytes);
        digest.AppendData([0]);
    }
}
