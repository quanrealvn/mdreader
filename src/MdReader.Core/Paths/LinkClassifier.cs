namespace MdReader.Core.Paths;

/// <summary>
/// Decides what a click on a link in an untrusted document does (ARCHITECTURE §4.3 classification table, §8.4 link
/// routing). Everything is decided lexically; the file system is probed only for a local, non-Markdown target (to
/// find a folder's README), and never for a UNC path on another share than the document's.
/// </summary>
public sealed class LinkClassifier
{
    internal const string UnsupportedLinkReason = "This type of link isn't opened by MdReader.";
    internal const string NotMarkdownReason = "Only Markdown files open in MdReader.";

    private const string ReservedDomain = "mdreader.example";

    private static readonly LinkTarget.Blocked Unsupported = new(UnsupportedLinkReason);
    private static readonly LinkTarget.Blocked NotMarkdown = new(NotMarkdownReason);

    private readonly IFileSystemProbe _fileSystem;

    public LinkClassifier(IFileSystemProbe fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _fileSystem = fileSystem;
    }

    /// rawHref = the author's attribute value as sent by links.js. resourceRoot null → document folder.
    public LinkTarget Classify(string rawHref, string documentPath, string? resourceRoot = null)
    {
        ArgumentNullException.ThrowIfNull(documentPath);

        var reference = ReferenceParser.Parse(rawHref);
        return reference.Kind switch
        {
            ReferenceKind.Fragment => new LinkTarget.Anchor(reference.Fragment ?? string.Empty),
            ReferenceKind.Http or ReferenceKind.Https => ClassifyWeb(reference.AbsoluteUri),
            ReferenceKind.Relative or ReferenceKind.RootRelative or ReferenceKind.WindowsAbsolute
                or ReferenceKind.File or ReferenceKind.Unc => ClassifyLocal(reference, documentPath, resourceRoot),
            _ => Unsupported,   // Empty, Mailto, OtherScheme, Invalid
        };
    }

    private static LinkTarget ClassifyWeb(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri
            || !(uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                 || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return Unsupported;
        }

        // Credentials in links are a phishing vector ("https://github.com@evil.example/"); also catches "https://@host".
        if (uri.UserInfo.Length > 0 || AuthorityContainsAt(uri.OriginalString))
        {
            return Unsupported;
        }

        // The app's own virtual hosts (app./doc.mdreader.example) are meaningless outside the WebView.
        if (IsReservedHost(uri.Host) || IsReservedHost(uri.IdnHost))
        {
            return Unsupported;
        }

        return new LinkTarget.External(uri);
    }

    private LinkTarget ClassifyLocal(ParsedReference reference, string documentPath, string? resourceRoot)
    {
        var document = LocalPathResolver.NormalizeFullPath(documentPath);
        if (document is null)
        {
            return Unsupported;
        }

        // "?query" (empty path) refers to the current document, like in a browser.
        if (reference.Kind == ReferenceKind.Relative && reference.DecodedPath is { Length: 0 })
        {
            return new LinkTarget.Anchor(reference.Fragment ?? string.Empty);
        }

        var documentDirectory = Path.GetDirectoryName(document) ?? document;
        var root = resourceRoot is null ? documentDirectory : LocalPathResolver.NormalizeFullPath(resourceRoot) ?? documentDirectory;

        // Lexical only: null for forbidden or malformed paths.
        var target = LocalPathResolver.Resolve(reference, documentDirectory, root);
        if (target is null)
        {
            return Unsupported;
        }

        // UNC / NTLM rule, checked lexically before any probe: a UNC target (from "\\srv\x", "//srv/x", a UNC file:
        // URI, or anything else that resolved to one) is allowed only on the document's own \\server\share.
        if (LocalPathResolver.IsUncPath(target) && !LocalPathResolver.IsSameUncShare(target, document))
        {
            return Unsupported;
        }

        if (LocalPathResolver.PathEquals(target, document))
        {
            return new LinkTarget.Anchor(reference.Fragment ?? string.Empty);
        }

        if (MarkdownFileTypes.IsMarkdownPath(target))
        {
            // Existence is not checked: a missing target opens a tab that shows "not found".
            return new LinkTarget.MarkdownDocument(target, reference.Fragment);
        }

        if (_fileSystem.DirectoryExists(target))
        {
            foreach (var indexName in MarkdownFileTypes.DirectoryIndexNames)
            {
                var index = Path.Join(target, indexName);
                if (_fileSystem.FileExists(index))
                {
                    return LocalPathResolver.PathEquals(index, document)
                        ? new LinkTarget.Anchor(reference.Fragment ?? string.Empty)
                        : new LinkTarget.MarkdownDocument(index, reference.Fragment);
                }
            }
        }

        return NotMarkdown;
    }

    private static bool IsReservedHost(string host)
    {
        var name = host.AsSpan().TrimEnd('.');
        return name.Equals(ReservedDomain, StringComparison.OrdinalIgnoreCase)
               || (name.EndsWith(ReservedDomain, StringComparison.OrdinalIgnoreCase) && name[^(ReservedDomain.Length + 1)] == '.');
    }

    /// <summary>True if the authority part ("scheme://authority/…") of the original string contains '@'.</summary>
    private static bool AuthorityContainsAt(string original)
    {
        var colon = original.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return false;
        }

        var rest = original.AsSpan(colon + 1).TrimStart("/\\");
        var end = rest.IndexOfAny("/\\?#");
        return (end < 0 ? rest : rest[..end]).Contains('@');
    }
}
