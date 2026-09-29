using MdReader.Core.Documents;

namespace MdReader.Core.Net;

/// <summary>Limits for <see cref="HttpMarkdownFetcher"/> (ARCHITECTURE §8). Every one of them is a hard stop.</summary>
public sealed record MarkdownFetchOptions
{
    /// <summary>The same ceiling a local file has, so "larger than MdReader can open" is one number.</summary>
    public const long DefaultMaxBytes = DocumentLoaderOptions.DefaultMaxBytes;

    /// <summary>Decompressed bytes. A gzip bomb is caught here, not by <c>Content-Length</c>.</summary>
    public long MaxBytes { get; init; } = DefaultMaxBytes;

    /// <summary>One budget for the whole fetch: connect, headers, every redirect hop and the body.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for a TCP connection on a single hop.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Redirects are followed by hand; each hop is re-validated. 0 means "don't follow any".</summary>
    public int MaxRedirects { get; init; } = 5;

    /// <summary>Code page used when the bytes aren't valid UTF-8, exactly as for a local file.</summary>
    public int FallbackCodePage { get; init; } = 1252;
}

/// <summary>Why a fetch didn't produce a document. The wording lives in <see cref="DocumentErrorMessages"/>.</summary>
public enum FetchError
{
    /// <summary>404 or 410.</summary>
    NotFound,

    /// <summary>401 or 403. MdReader never signs in, so this is the end of the road.</summary>
    AccessDenied,

    /// <summary>Over <see cref="MarkdownFetchOptions.MaxBytes"/>, by header or by what actually arrived.</summary>
    TooLarge,

    /// <summary>The whole fetch ran past <see cref="MarkdownFetchOptions.Timeout"/>.</summary>
    Timeout,

    /// <summary>DNS, TLS, connection refused, connection dropped — nothing was delivered.</summary>
    Unreachable,

    /// <summary>The body decoded as binary (NUL bytes), like a local file that isn't text.</summary>
    NotText,

    /// <summary>The server said the response is something other than text (a web page, an image, a PDF).</summary>
    NotMarkdown,

    /// <summary>MdReader itself stopped: a redirect that left http(s), a credentialed target, too many hops.</summary>
    Blocked,

    /// <summary>Any other status the server returned.</summary>
    ServerError,
}

/// <summary>A finished fetch: the document, or the reason there isn't one. Never an exception.</summary>
public abstract record FetchResult
{
    private FetchResult()
    {
    }

    /// <param name="Url">The address the document actually came from (after redirects); the base for relative links.</param>
    public sealed record Success(Uri Url, string Text, string EncodingName, bool UsedFallbackEncoding, long ByteLength)
        : FetchResult;

    /// <param name="Detail">One sentence naming what happened, safe to show and to log.</param>
    public sealed record Failure(FetchError Error, string Detail) : FetchResult;
}

/// <summary>Fetches a Markdown document over http(s). Implementations never throw for a network problem.</summary>
public interface IMarkdownFetcher
{
    Task<FetchResult> FetchAsync(Uri url, CancellationToken cancellationToken = default);
}
