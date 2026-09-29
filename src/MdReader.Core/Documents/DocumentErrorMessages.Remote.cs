using MdReader.Core.Net;
using MdReader.Core.Protocol;

namespace MdReader.Core.Documents;

/// <summary>
/// The wording for a document that came (or didn't come) from an http(s) address (ARCHITECTURE §8, §9). It lives here
/// with the rest of the user-facing text rather than in <c>MdReader.Core.Net</c>, so there is one place to read when
/// someone asks what MdReader tells people.
/// </summary>
/// <remarks>
/// The details these messages carry are composed by <see cref="HttpMarkdownFetcher"/> out of status codes, media types
/// and socket errors — never out of a path — so unlike <see cref="For"/> they are not run through the path scrubber,
/// which would eat parts of them on POSIX. They are still collapsed and capped: a server chooses its own reason phrase.
/// </remarks>
public static partial class DocumentErrorMessages
{
    /// <summary>The error view for a fetch that failed: which kind the page shows, and what it says.</summary>
    public static (DocumentErrorKind Kind, string Title, string Message) ForFetch(FetchError error, string? detail)
    {
        var text = ShortenRemoteDetail(detail);
        return error switch
        {
            FetchError.NotFound => (
                DocumentErrorKind.NotFound,
                "Not found",
                WithDetail("There is no document at this address.", text)),

            FetchError.AccessDenied => (
                DocumentErrorKind.AccessDenied,
                "Access denied",
                WithDetail("The server refused the request. MdReader doesn't sign in to websites.", text)),

            FetchError.TooLarge => (
                DocumentErrorKind.TooLarge,
                "Document is too large",
                text is null
                    ? "This document is larger than MdReader can open."
                    : $"This document is too large to display ({text.TrimEnd('.')})."),

            FetchError.Timeout => (
                DocumentErrorKind.IoError,
                "The server didn't answer",
                WithDetail("MdReader gave up waiting for this document.", text)),

            FetchError.Unreachable => (
                DocumentErrorKind.IoError,
                "Couldn't reach the server",
                WithDetail("MdReader couldn't connect to this address.", text)),

            FetchError.NotText => (
                DocumentErrorKind.Binary,
                "This doesn't look like a text file",
                "MdReader shows Markdown and other text, but what the server sent contains binary data."),

            FetchError.NotMarkdown => (
                DocumentErrorKind.IoError,
                "That isn't a Markdown document",
                WithDetail("MdReader opens Markdown, not web pages or other files.", text)),

            FetchError.Blocked => (
                DocumentErrorKind.IoError,
                "MdReader stopped here",
                WithDetail("This address isn't one MdReader will open.", text)),

            _ => (
                DocumentErrorKind.IoError,
                "The server had a problem",
                WithDetail("The document couldn't be fetched.", text)),
        };
    }

    /// <summary>
    /// A refresh (F5, "Try again") of a document already on screen failed: the document stays, the banner explains.
    /// Nothing about a web address is permanent, so none of these throw the content away.
    /// </summary>
    public static BannerInfo FetchFailedBanner(FetchError error, string? detail)
    {
        var text = ShortenRemoteDetail(detail);
        return error switch
        {
            FetchError.NotFound => new(BannerKind.Warning,
                "Couldn't reload: there is no document at this address any more. " + ShowingLastVersion),

            FetchError.AccessDenied => new(BannerKind.Error,
                "Couldn't reload: the server refused the request. " + ShowingLastVersion),

            FetchError.Timeout or FetchError.Unreachable => new(BannerKind.Warning,
                "Couldn't reload: MdReader couldn't reach the server. " + ShowingLastVersion),

            FetchError.TooLarge => new(BannerKind.Error,
                (text is null
                    ? "Couldn't reload: the document is now too large to display. "
                    : $"Couldn't reload: the document is now too large to display ({text.TrimEnd('.')}). ")
                + ShowingLastVersion),

            FetchError.NotText or FetchError.NotMarkdown => new(BannerKind.Error,
                "Couldn't reload: the server no longer sends a Markdown document. " + ShowingLastVersion),

            _ => new(BannerKind.Error,
                WithDetail("Couldn't reload the document.", text) + " " + ShowingLastVersion),
        };
    }

    /// <summary>Collapses whitespace and caps the length; null when nothing useful is left.</summary>
    private static string? ShortenRemoteDetail(string? detail)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        var text = CollapseWhitespace(detail.Length > MaxDetailLength ? detail[..MaxDetailLength] : detail);
        return text.Length == 0 ? null : text;
    }
}
