using System.Buffers;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using MdReader.Core.Protocol;
using MdReader.Core.Rendering;
using Microsoft.AspNetCore.RateLimiting;

namespace MdReader.Web;

/// <summary>
/// <c>POST /api/render</c> (ARCHITECTURE §15): <c>{"markdown": "..."}</c> → <c>{"messages": [render, renderPart...]}</c>,
/// exactly the host messages the desktop app posts to its page. Privacy: the document is only held in memory for the
/// request, and nothing derived from it is logged — log lines carry sizes, timings and status only.
/// </summary>
internal static class RenderEndpoint
{
    public const string Path = "/api/render";
    public const string RateLimitPolicy = "render";
    public const string LogCategory = "MdReader.Web.Render";

    private const int DocId = 1;
    private const int ReadChunkBytes = 64 * 1024;
    private const string BusyRetryAfterSeconds = "2";

    /// The server never touches its own file system because of document content: every local image reference is
    /// dropped without a file-system call (AllowLocalResources = false). The paths are placeholders.
    private static readonly RenderContext Context = new("/document.md", "/") { AllowLocalResources = false };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static int s_version;

    public static RouteHandlerBuilder MapRenderEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost(Path, HandleAsync).RequireRateLimiting(RateLimitPolicy);

    public static async Task HandleAsync(HttpContext context, IMarkdownRenderer renderer, RenderGate gate, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(LogCategory);
        var started = Stopwatch.GetTimestamp();
        var request = context.Request;
        var cancellationToken = context.RequestAborted;
        context.Response.Headers.CacheControl = "no-store";

        if (!IsJson(request.ContentType))
        {
            logger.LogInformation("Render rejected: unsupported content type (415)");
            await WriteErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, "badRequest",
                "Send the document as JSON: {\"markdown\": \"...\"}.");
            return;
        }

        if (request.ContentLength > WebLimitsOptions.MaxRequestBodyBytes)
        {
            logger.LogInformation("Render rejected: body of {Bytes} bytes is too large (413)", request.ContentLength);
            await WriteTooLargeAsync(context);
            return;
        }

        byte[]? body;
        try
        {
            body = await ReadBodyAsync(request.Body, request.ContentLength, WebLimitsOptions.MaxRequestBodyBytes, cancellationToken);
        }
        catch (BadHttpRequestException ex)
        {
            // Kestrel's own MaxRequestBodySize (413) or a malformed/aborted upload.
            logger.LogInformation("Render rejected: the request body could not be read ({Status})", ex.StatusCode);
            if (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                await WriteTooLargeAsync(context);
            }
            else
            {
                await WriteErrorAsync(context, ex.StatusCode, "badRequest", "The request could not be read.");
            }

            return;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            logger.LogInformation("Render abandoned: the client went away while uploading");
            return;
        }

        if (body is null)
        {
            logger.LogInformation("Render rejected: body is larger than {Limit} bytes (413)", WebLimitsOptions.MaxRequestBodyBytes);
            await WriteTooLargeAsync(context);
            return;
        }

        string? markdown;
        try
        {
            markdown = JsonSerializer.Deserialize(body, WebJsonContext.Default.RenderRequest)?.Markdown;
        }
        catch (JsonException)
        {
            // Never log the exception message: it quotes the offending character of the document.
            markdown = null;
        }

        if (markdown is null)
        {
            logger.LogInformation("Render rejected: {Bytes} bytes of invalid JSON or no \"markdown\" string (400)", body.Length);
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "badRequest",
                "The request must be a JSON object with a \"markdown\" string.");
            return;
        }

        body = null; // the decoded string is all that's needed from here on

        RateLimitLease lease;
        try
        {
            lease = await gate.AcquireAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Render abandoned: the client went away while queued");
            return;
        }

        using (lease)
        {
            if (!lease.IsAcquired)
            {
                logger.LogWarning("Render rejected: the server is busy (503)");
                context.Response.Headers.RetryAfter = BusyRetryAfterSeconds;
                await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "busy",
                    "The server is busy. Try again in a moment.");
                return;
            }

            IReadOnlyList<string> messages;
            RenderTimings timings;
            try
            {
                var version = Interlocked.Increment(ref s_version);
                (messages, timings) = await Task.Run(() =>
                {
                    var result = renderer.Render(markdown, Context, cancellationToken);
                    var serialized = ProtocolSerializer.SerializeRender(DocId, version, result, preserveScroll: false,
                        scrollToId: null, banner: null);
                    return (serialized, result.Timings);
                }, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("Render abandoned: the client went away after {ElapsedMs:0} ms", Elapsed(started));
                return;
            }
            catch (Exception ex)
            {
                // The type and stack only: an exception message could quote document content.
                logger.LogError("Render failed for {Chars} chars: {ExceptionType}{NewLine}{StackTrace}",
                    markdown.Length, ex.GetType().FullName, Environment.NewLine, ex.StackTrace);
                await WriteErrorAsync(context, StatusCodes.Status500InternalServerError, "renderFailed",
                    "This document couldn't be rendered.");
                return;
            }

            var outputChars = 0L;
            try
            {
                outputChars = await WriteMessagesAsync(context.Response, messages, cancellationToken);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                logger.LogInformation("Render abandoned: the client went away while downloading");
                return;
            }

            logger.LogInformation(
                "Rendered {Chars} chars into {Parts} part(s), {OutputChars} chars of JSON; render {RenderMs:0.0} ms, total {ElapsedMs:0.0} ms",
                markdown.Length, messages.Count, outputChars, timings.TotalMs, Elapsed(started));
        }
    }

    /// 429 from the rate limiter (<see cref="RateLimiterOptions.OnRejected"/>).
    public static async ValueTask OnRateLimitedAsync(OnRejectedContext rejected, CancellationToken cancellationToken)
    {
        var context = rejected.HttpContext;
        var retryAfter = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) && wait > TimeSpan.Zero
            ? wait
            : TimeSpan.FromMinutes(1);
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));

        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory)
            .LogInformation("Render rejected: rate limit reached (429, retry after {Seconds} s)", seconds);

        context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Response.Headers.CacheControl = "no-store";
        await WriteErrorAsync(context, StatusCodes.Status429TooManyRequests, "rateLimited",
            $"Too many requests. Try again in {seconds} seconds.", cancellationToken);
    }

    private static bool IsJson(string? contentType) =>
        MediaTypeHeaderValue.TryParse(contentType, out var parsed)
        && (string.Equals(parsed.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || (parsed.MediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ?? false));

    /// Reads the whole body, or returns null as soon as it exceeds <paramref name="limit"/> bytes.
    private static async Task<byte[]?> ReadBodyAsync(Stream body, long? contentLength, int limit, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream((int)Math.Clamp(contentLength ?? ReadChunkBytes, 0, limit));
        var chunk = ArrayPool<byte>.Shared.Rent(ReadChunkBytes);
        try
        {
            int read;
            while ((read = await body.ReadAsync(chunk.AsMemory(0, ReadChunkBytes), cancellationToken)) > 0)
            {
                if (buffer.Length + read > limit)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return buffer.ToArray();
    }

    /// Writes <c>{"messages":[m0,m1,...]}</c>; each message is already a JSON object. Returns the chars written.
    private static async Task<long> WriteMessagesAsync(HttpResponse response, IReadOnlyList<string> messages, CancellationToken cancellationToken)
    {
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/json; charset=utf-8";

        var written = 0L;
        await using var writer = new StreamWriter(response.Body, Utf8NoBom, ReadChunkBytes, leaveOpen: true);
        await writer.WriteAsync("{\"messages\":[");
        for (var index = 0; index < messages.Count; index++)
        {
            if (index > 0)
            {
                await writer.WriteAsync(',');
            }

            await writer.WriteAsync(messages[index].AsMemory(), cancellationToken);
            written += messages[index].Length;
        }

        await writer.WriteAsync("]}");
        await writer.FlushAsync(cancellationToken);
        return written + Math.Max(0, messages.Count - 1) + "{\"messages\":[]}".Length;
    }

    private static Task WriteTooLargeAsync(HttpContext context) =>
        WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "tooLarge",
            "This document is over 2 MB.");

    private static async Task WriteErrorAsync(HttpContext context, int status, string error, string message,
        CancellationToken cancellationToken = default)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(context.Response.Body, new ErrorResponse(error, message),
            WebJsonContext.Default.ErrorResponse, cancellationToken);
    }

    private static double Elapsed(long started) => Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
