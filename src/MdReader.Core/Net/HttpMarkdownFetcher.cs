using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using MdReader.Core.Documents;

namespace MdReader.Core.Net;

/// <summary>
/// MdReader's only outbound request (ARCHITECTURE §8). It fetches one Markdown document and nothing else, and it is
/// built so that a hostile address can't turn it into something more useful than that:
/// <list type="bullet">
/// <item>no credentials of any kind. <see cref="SocketsHttpHandler"/> has no "use the logged-on user"; here
/// <see cref="SocketsHttpHandler.Credentials"/> and <see cref="SocketsHttpHandler.DefaultProxyCredentials"/> are
/// explicitly null and <see cref="SocketsHttpHandler.PreAuthenticate"/> is false, so a <c>401 WWW-Authenticate:
/// Negotiate</c> from a stranger's server gets no NTLM handshake and no hash — it is simply "access denied";</item>
/// <item>no cookies, no stored credentials, no client certificate, nothing that identifies the user;</item>
/// <item>redirects are followed by hand and every hop goes through <see cref="DocumentUrl.IsAllowed(Uri, out string)"/>,
/// so a <c>Location:</c> that leaves http(s) (<c>file:</c>, <c>data:</c>) or gains credentials ends the fetch;</item>
/// <item>one wall-clock budget covers connect, headers, every hop and the body, so a server that answers one byte a
/// minute can't hold a tab open forever;</item>
/// <item>the size limit is enforced on the bytes that arrive after decompression, so a gzip bomb hits it.</item>
/// </list>
/// The result is bytes, decoded by the same <see cref="TextDecoder"/> a local file goes through, and from there it is
/// exactly as untrusted as any other document: same sanitizer, same budgets.
/// </summary>
public sealed class HttpMarkdownFetcher : IMarkdownFetcher, IDisposable
{
    /// <summary>What we ask for. Servers that ignore it send whatever they like; that is what the checks below are for.</summary>
    private const string AcceptHeader = "text/markdown, text/plain;q=0.9, */*;q=0.1";

    /// <summary>Read in chunks so a slow trickle is noticed by the timeout rather than by a stalled big read.</summary>
    private const int ReadChunkBytes = 64 * 1024;

    /// <summary>A response that is text but is a web page: not what "open this Markdown document" asked for.</summary>
    private static readonly string[] RefusedMediaTypes = ["text/html", "application/xhtml+xml"];

    /// <summary>Types that may carry Markdown. Anything else (image, PDF, JSON, …) is refused by its own name.</summary>
    private static readonly string[] AllowedNonTextMediaTypes =
        ["application/markdown", "application/x-markdown", "application/octet-stream"];

    private readonly MarkdownFetchOptions _options;
    private readonly TimeProvider _time;
    private readonly HttpClient _client;
    private bool _disposed;

    public HttpMarkdownFetcher(MarkdownFetchOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new MarkdownFetchOptions();
        _time = timeProvider ?? TimeProvider.System;

        var handler = new SocketsHttpHandler
        {
            // Every redirect is a decision this class makes itself (see FetchCoreAsync).
            AllowAutoRedirect = false,
            UseCookies = false,
            Credentials = null,
            DefaultProxyCredentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = _options.ConnectTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        };

        // The timeout is ours (HttpClient.Timeout would stop at the response headers when the body is streamed).
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent());
        _client.DefaultRequestHeaders.Accept.ParseAdd(AcceptHeader);
    }

    public async Task<FetchResult> FetchAsync(Uri url, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!DocumentUrl.IsAllowed(url, out var reason))
        {
            return new FetchResult.Failure(FetchError.Blocked, reason);
        }

        using var budget = new CancellationTokenSource(_options.Timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        try
        {
            return await FetchCoreAsync(url, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return new FetchResult.Failure(FetchError.Timeout,
                $"The server didn't finish answering within {_options.Timeout.TotalSeconds:0} seconds.");
        }
        catch (HttpRequestException ex)
        {
            return new FetchResult.Failure(FetchError.Unreachable, Describe(ex));
        }
        catch (IOException ex)
        {
            return new FetchResult.Failure(FetchError.Unreachable, Describe(ex));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.Dispose();
    }

    private async Task<FetchResult> FetchCoreAsync(Uri url, CancellationToken cancellationToken)
    {
        var current = url;
        for (var hop = 0; ; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                if (hop >= _options.MaxRedirects)
                {
                    return new FetchResult.Failure(FetchError.Blocked,
                        $"The address kept redirecting (more than {_options.MaxRedirects} times).");
                }

                if (!TryNextHop(current, response, out var next, out var failure))
                {
                    return failure;
                }

                current = next;
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                return FromStatus(response);
            }

            return await ReadDocumentAsync(current, response, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Resolves a <c>Location</c> against the address it came from and re-validates it. True when
    /// <paramref name="next"/> is a hop we may follow; otherwise <paramref name="failure"/> says why not.
    /// </summary>
    private static bool TryNextHop(Uri current, HttpResponseMessage response, [NotNullWhen(true)] out Uri? next,
                                   [NotNullWhen(false)] out FetchResult.Failure? failure)
    {
        next = null;

        var location = response.Headers.Location;
        if (location is null)
        {
            failure = new FetchResult.Failure(FetchError.ServerError,
                $"The server answered {StatusText(response)} without saying where to go next.");
            return false;
        }

        Uri resolved;
        try
        {
            resolved = location.IsAbsoluteUri ? location : new Uri(current, location);
        }
        catch (UriFormatException)
        {
            failure = new FetchResult.Failure(FetchError.Blocked, "The server redirected to an address MdReader couldn't read.");
            return false;
        }

        if (!DocumentUrl.IsAllowed(resolved, out var reason))
        {
            // The interesting case: "Location: file:///C:/Users/…". The fetch stops here; nothing local is touched.
            failure = new FetchResult.Failure(FetchError.Blocked, "The server redirected somewhere MdReader won't follow. " + reason);
            return false;
        }

        next = resolved;
        failure = null;
        return true;
    }

    private async Task<FetchResult> ReadDocumentAsync(Uri url, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // A Content-Type that is present but won't parse ("text/html;;;", `text/HTML;x="y`) leaves ContentType null,
        // which would otherwise read as "no type at all" and be accepted. A header we can't understand is a header we
        // can't vouch for, so it is refused by the same rule as a type we don't want.
        if (response.Content.Headers.ContentType is null && response.Content.Headers.TryGetValues("Content-Type", out var raw))
        {
            return new FetchResult.Failure(FetchError.NotMarkdown,
                $"The server sent a content type MdReader couldn't read ({Describe(string.Join(", ", raw))}).");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!IsAcceptableMediaType(mediaType))
        {
            return new FetchResult.Failure(FetchError.NotMarkdown, $"The server sent {Describe(mediaType)}.");
        }

        var max = Math.Clamp(_options.MaxBytes, 0, Array.MaxLength - 1);
        var declared = response.Content.Headers.ContentLength;
        if (declared is { } length && length > max)
        {
            // Refused before a single byte of the body is read.
            return new FetchResult.Failure(FetchError.TooLarge, DocumentErrorMessages.TooLargeDetail(length, max));
        }

        var capacity = (int)Math.Clamp(declared ?? 0, 0, 1024 * 1024);
        using var buffer = new MemoryStream(capacity);
        var chunk = new byte[ReadChunkBytes];
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > max)
                {
                    // Counted after decompression, so a small gzip body that expands past the limit stops here. The
                    // response is abandoned with it: nothing reads the rest.
                    return new FetchResult.Failure(FetchError.TooLarge,
                        $"larger than {DocumentErrorMessages.FormatSize(max)}");
                }

                buffer.Write(chunk, 0, read);
            }
        }

        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        var decoded = TextDecoder.Decode(bytes, _options.FallbackCodePage);
        if (decoded.IsBinary)
        {
            return new FetchResult.Failure(FetchError.NotText, DocumentErrorMessages.BinaryDetail);
        }

        return new FetchResult.Success(url, decoded.Text, decoded.EncodingName, decoded.UsedFallbackEncoding, buffer.Length);
    }

    /// <summary>
    /// Text is fine (that is what Markdown is), except a web page: <c>text/html</c> would be an HTML document run
    /// through the Markdown parser, which is not what the user asked for and reads like nonsense. A missing type is
    /// accepted — plenty of raw-content hosts send none — and so is <c>application/octet-stream</c>, which is what
    /// several of them send for a <c>.md</c> file.
    /// </summary>
    private static bool IsAcceptableMediaType(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return true;
        }

        foreach (var refused in RefusedMediaTypes)
        {
            if (mediaType.Equals(refused, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var allowed in AllowedNonTextMediaTypes)
        {
            if (mediaType.Equals(allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently
        or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect
        or HttpStatusCode.PermanentRedirect;

    private static FetchResult.Failure FromStatus(HttpResponseMessage response)
    {
        var error = response.StatusCode switch
        {
            HttpStatusCode.NotFound or HttpStatusCode.Gone => FetchError.NotFound,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => FetchError.AccessDenied,
            _ => FetchError.ServerError,
        };

        return new FetchResult.Failure(error, $"The server answered {StatusText(response)}.");
    }

    /// <summary>"404 (Not Found)". The reason phrase comes from the server, so it is capped and stripped of controls.</summary>
    private static string StatusText(HttpResponseMessage response)
    {
        var code = ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
        var phrase = response.ReasonPhrase;
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return code;
        }

        var cleaned = Clean(phrase);
        return cleaned.Length == 0 ? code : $"{code} ({cleaned})";
    }

    /// <summary>A media type named back to the user, capped and stripped of controls (the server chose it).</summary>
    private static string Describe(string? mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
        {
            return "something that isn't a document";
        }

        var cleaned = Clean(mediaType);
        return cleaned.Length == 0 ? "something that isn't a document" : cleaned;
    }

    /// <summary>Server-chosen text, made safe to show and to log: no control characters, at most 60 of them.</summary>
    private static string Clean(string value)
    {
        var length = Math.Min(value.Length, 60);
        var buffer = new char[length];
        for (var i = 0; i < length; i++)
        {
            buffer[i] = char.IsControl(value[i]) ? ' ' : value[i];
        }

        return new string(buffer).Trim();
    }

    /// <summary>The innermost message of a network failure: the outer ones only repeat "an error occurred".</summary>
    private static string Describe(Exception exception)
    {
        var current = exception;
        while (current.InnerException is { } inner)
        {
            current = inner;
        }

        return current.Message;
    }

    private static string UserAgent()
    {
        var version = typeof(HttpMarkdownFetcher).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        return "MdReader/" + version;
    }
}
