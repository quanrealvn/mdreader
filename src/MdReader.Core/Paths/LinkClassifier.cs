namespace MdReader.Core.Paths;

/// <summary>
/// Decides what a click on a link in an untrusted document does (ARCHITECTURE §4.3 classification table, §8.4 link
/// routing). Everything is decided lexically; the file system is probed only for a local, non-Markdown target (to
/// find a folder's README), and never for a UNC path on another share than the document's.
/// </summary>
public sealed class LinkClassifier
{
    internal const string UnsupportedLinkReason = "This type of link isn't opened by MdReader.";
    /// <summary>Shown when a link names a file this application does not read. The name predates JSON.</summary>
    internal const string NotMarkdownReason = "Only Markdown and JSON files open in MdReader.";

    private static readonly LinkTarget.Blocked Unsupported = new(UnsupportedLinkReason);
    private static readonly LinkTarget.Blocked NotMarkdown = new(NotMarkdownReason);

    private readonly IFileSystemProbe _fileSystem;
    private readonly PathPolicy _policy;

    public LinkClassifier(IFileSystemProbe fileSystem, PathPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
        _policy = policy ?? PathPolicy.Current;
    }

    /// rawHref = the author's attribute value as sent by links.js. resourceRoot null → document folder.
    public LinkTarget Classify(string rawHref, string documentPath, string? resourceRoot = null)
    {
        ArgumentNullException.ThrowIfNull(documentPath);

        var reference = ReferenceParser.Parse(rawHref, _policy);
        return reference.Kind switch
        {
            ReferenceKind.Fragment => new LinkTarget.Anchor(reference.Fragment ?? string.Empty),
            ReferenceKind.Http or ReferenceKind.Https => ClassifyWeb(reference.AbsoluteUri, insideWeb: false),
            ReferenceKind.Relative or ReferenceKind.RootRelative or ReferenceKind.WindowsAbsolute
                or ReferenceKind.File or ReferenceKind.Unc => ClassifyLocal(reference, documentPath, resourceRoot),
            _ => Unsupported,   // Empty, Mailto, OtherScheme, Invalid
        };
    }

    /// <summary>
    /// The same decision for a document that was fetched from <paramref name="documentUrl"/> (§8). Relative links
    /// resolve against that address, exactly as they would in a browser; a target that names a Markdown file opens in
    /// MdReader, any other web address goes to the browser, and anything that names this machine is blocked — a
    /// document from the internet may not offer to open a local file.
    /// </summary>
    public LinkTarget ClassifyRemote(string rawHref, Uri documentUrl)
    {
        ArgumentNullException.ThrowIfNull(documentUrl);

        var reference = ReferenceParser.Parse(rawHref, _policy);
        switch (reference.Kind)
        {
            case ReferenceKind.Fragment:
                return new LinkTarget.Anchor(reference.Fragment ?? string.Empty);

            case ReferenceKind.Http or ReferenceKind.Https:
                return ClassifyWeb(reference.AbsoluteUri, insideWeb: true);

            // "?query" or "" (an empty path) refers to the current document, like in a browser.
            case ReferenceKind.Relative when reference.DecodedPath is { Length: 0 }:
                return new LinkTarget.Anchor(reference.Fragment ?? string.Empty);

            case ReferenceKind.Relative:
            case ReferenceKind.RootRelative:
            case ReferenceKind.Unc when ResolvesAgainstBase(reference.Raw):
                return ResolveAgainst(documentUrl, reference.Raw);

            default:
                // WindowsAbsolute, file:, "\\server\share", mailto:, unknown schemes.
                return Unsupported;
        }
    }

    /// <summary>Only "//host/x" is a protocol-relative URL; "\\server\share" and "/\host" name a machine.</summary>
    private static bool ResolvesAgainstBase(string raw)
    {
        var cleaned = ReferenceParser.CleanUrl(raw);
        return cleaned.Length >= 2 && cleaned[0] == '/' && cleaned[1] == '/';
    }

    private static LinkTarget ResolveAgainst(Uri documentUrl, string raw)
    {
        var cleaned = ReferenceParser.CleanUrl(raw);
        return Uri.TryCreate(documentUrl, cleaned, out var absolute) ? ClassifyWeb(absolute, insideWeb: true) : Unsupported;
    }

    /// <param name="insideWeb">
    /// True when the link was clicked in a document that itself came from the web. A Markdown target then opens in
    /// MdReader rather than in the browser; from a local document an http(s) link always goes to the browser, exactly
    /// as it did before.
    /// </param>
    private static LinkTarget ClassifyWeb(Uri? uri, bool insideWeb)
    {
        if (uri is null || !uri.IsAbsoluteUri
            || !(uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                 || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return Unsupported;
        }

        // Credentials in links are a phishing vector ("https://github.com@evil.example/"); also catches "https://@host".
        if (WebUrlPolicy.HasCredentials(uri))
        {
            return Unsupported;
        }

        // The app's own virtual hosts (app./doc.mdreader.example) are meaningless outside the WebView.
        if (WebUrlPolicy.IsReservedHost(uri))
        {
            return Unsupported;
        }

        // AbsolutePath has no query or fragment, and IsMarkdownPath is purely lexical: nothing here touches a disk.
        return insideWeb && MarkdownFileTypes.IsMarkdownPath(uri.AbsolutePath)
            ? new LinkTarget.RemoteDocument(uri, DecodeFragment(uri.Fragment))
            : new LinkTarget.External(uri);
    }

    /// <summary>"#a%20b" → "a b"; null when there is no fragment.</summary>
    private static string? DecodeFragment(string fragment)
    {
        if (fragment.Length <= 1)
        {
            return null;
        }

        var value = fragment[1..];
        return value.Contains('%', StringComparison.Ordinal) ? Uri.UnescapeDataString(value) : value;
    }

    private LinkTarget ClassifyLocal(ParsedReference reference, string documentPath, string? resourceRoot)
    {
        var document = _policy.NormalizeFullPath(documentPath);
        if (document is null)
        {
            return Unsupported;
        }

        // "?query" (empty path) refers to the current document, like in a browser.
        if (reference.Kind == ReferenceKind.Relative && reference.DecodedPath is { Length: 0 })
        {
            return new LinkTarget.Anchor(reference.Fragment ?? string.Empty);
        }

        var documentDirectory = _policy.GetDirectoryName(document) ?? document;
        var root = resourceRoot is null ? documentDirectory : _policy.NormalizeFullPath(resourceRoot) ?? documentDirectory;

        // Lexical only: null for forbidden or malformed paths.
        var target = LocalPathResolver.Resolve(reference, documentDirectory, root, _policy);
        if (target is null)
        {
            return Unsupported;
        }

        // Network / NTLM rule, checked lexically before any probe: a target that could reach another machine (from
        // "\\srv\x", "//srv/x", a UNC file: URI, macOS's /net/<host>, or anything else that resolved to one) is
        // allowed only on the host and share the document itself lives on.
        if (_policy.IsNetworkPath(target) && !_policy.IsSameNetworkRoot(target, document))
        {
            return Unsupported;
        }

        if (_policy.PathEquals(target, document))
        {
            return new LinkTarget.Anchor(reference.Fragment ?? string.Empty);
        }

        if (DocumentFileTypes.IsDocumentPath(target))
        {
            // Existence is not checked: a missing target opens a tab that shows "not found".
            return new LinkTarget.MarkdownDocument(target, reference.Fragment);
        }

        if (_fileSystem.DirectoryExists(target))
        {
            foreach (var indexName in MarkdownFileTypes.DirectoryIndexNames)
            {
                var index = _policy.Join(target, indexName);
                if (_fileSystem.FileExists(index))
                {
                    return _policy.PathEquals(index, document)
                        ? new LinkTarget.Anchor(reference.Fragment ?? string.Empty)
                        : new LinkTarget.MarkdownDocument(index, reference.Fragment);
                }
            }
        }

        return NotMarkdown;
    }

}
