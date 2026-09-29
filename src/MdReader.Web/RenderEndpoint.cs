using System.Buffers;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using MdReader.Core.Protocol;
using MdReader.Core.Rendering;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

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

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private static int s_version;

    public static RouteHandlerBuilder MapRenderEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost(Path, HandleAsync).RequireRateLimiting(RateLimitPolicy);

    public static async Task HandleAsync(HttpContext context, IMarkdownRenderer renderer, RenderGate gate,
        RenderCostLimiter cost, IOptions<WebLimitsOptions> options, ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(LogCategory);
        var started = Stopwatch.GetTimestamp();
        var request = context.Request;
        var limits = options.Value;
        context.Response.Headers.CacheControl = "no-store";

        // One deadline over the whole request — upload, render and download — so no single client can hold a
        // connection (and, past the gate, a worker) for longer than this. RequestAborted stays the "client went
        // away" signal; this token also fires when the budget runs out, and the two are told apart below.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        if (limits.RequestBudget > TimeSpan.Zero)
        {
            deadline.CancelAfter(limits.RequestBudget);
        }

        var cancellationToken = deadline.Token;

        if (!IsJson(request.ContentType))
        {
            logger.LogInformation("Render rejected: unsupported content type (415)");
            await WriteErrorAsync(context, StatusCodes.Status415UnsupportedMediaType, "badRequest",
                "Send the document as JSON: {\"markdown\": \"...\"}.");
            return;
        }

        if (request.ContentLength > limits.MaxRequestBodyBytes)
        {
            logger.LogInformation("Render rejected: body of {Bytes} bytes is too large (413)", request.ContentLength);
            await WriteTooLargeAsync(context, limits);
            return;
        }

        // Charge the client's byte budget on the declared length, before the body is read: an expensive request is
        // turned away without ever being uploaded. A request with no declared length is charged the full cap.
        using (var costLease = await cost.AcquireAsync(context,
                   request.ContentLength ?? limits.MaxRequestBodyBytes, CancellationToken.None))
        {
            if (!costLease.IsAcquired)
            {
                var seconds = RetryAfterSeconds(costLease, limits);
                logger.LogInformation("Render rejected: byte budget spent (429, retry after {Seconds} s)", seconds);
                await WriteRateLimitedAsync(context, seconds, cancellationToken);
                return;
            }
        }

        byte[]? body;
        try
        {
            // WaitAsync, not just the token: the deadline has to hold whether or not the body stream honours a
            // cancellation token, and a stream that ignores it is exactly what a slow upload would be reading from.
            // Whatever is left of the read is abandoned when this request is answered and the connection torn down.
            body = await ReadBodyAsync(request.Body, request.ContentLength, limits.MaxRequestBodyBytes, cancellationToken)
                .WaitAsync(cancellationToken);
        }
        catch (BadHttpRequestException ex)
        {
            // Kestrel's own MaxRequestBodySize (413) or a malformed/aborted upload.
            logger.LogInformation("Render rejected: the request body could not be read ({Status})", ex.StatusCode);
            if (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                await WriteTooLargeAsync(context, limits);
            }
            else
            {
                await WriteErrorAsync(context, ex.StatusCode, "badRequest", "The request could not be read.");
            }

            return;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            if (context.RequestAborted.IsCancellationRequested)
            {
                logger.LogInformation("Render abandoned: the client went away while uploading");
                return;
            }

            logger.LogInformation("Render rejected: the upload took longer than {Budget} (408)", limits.RequestBudget);
            await WriteErrorAsync(context, StatusCodes.Status408RequestTimeout, "timeout",
                "The upload took too long.");
            return;
        }

        if (body is null)
        {
            logger.LogInformation("Render rejected: body is larger than {Limit} bytes (413)", limits.MaxRequestBodyBytes);
            await WriteTooLargeAsync(context, limits);
            return;
        }

        string? markdown;
        string? kind = null;
        try
        {
            var rendered = JsonSerializer.Deserialize(body, WebJsonContext.Default.RenderRequest);
            markdown = rendered?.Markdown;
            kind = rendered?.Kind;
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

        // An unknown kind is refused rather than quietly read as Markdown: a client that asks for something this
        // server does not have should be told so, not handed a JSON document rendered as prose.
        if (!RenderKinds.IsKnown(kind))
        {
            logger.LogInformation("Render rejected: unknown kind (400)");
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "badRequest",
                "\"kind\" must be \"markdown\" or \"json\" when it is given.");
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
            if (context.RequestAborted.IsCancellationRequested)
            {
                logger.LogInformation("Render abandoned: the client went away while queued");
                return;
            }

            logger.LogInformation("Render rejected: still queued after {Budget} (503)", limits.RequestBudget);
            context.Response.Headers.RetryAfter = BusyRetryAfterSeconds;
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, "busy",
                "The server is busy. Try again in a moment.");
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

            // The render's own, tighter deadline. Core checks this token inside Markdig's HTML writing and
            // AngleSharp's tree construction, so an abandoned render really stops instead of running on unseen.
            using var renderDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (limits.RenderBudget > TimeSpan.Zero)
            {
                renderDeadline.CancelAfter(limits.RenderBudget);
            }

            var renderToken = renderDeadline.Token;
            IReadOnlyList<string> messages;
            RenderTimings timings;
            try
            {
                var version = Interlocked.Increment(ref s_version);
                var renderContext = CreateRenderContext(limits, kind);
                (messages, timings) = await Task.Run(() =>
                {
                    var result = renderer.Render(markdown, renderContext, renderToken);
                    var serialized = ProtocolSerializer.SerializeRender(DocId, version, result, preserveScroll: false,
                        scrollToId: null, banner: null);
                    return (serialized, result.Timings);
                }, renderToken);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                logger.LogInformation("Render abandoned: the client went away after {ElapsedMs:0} ms", Elapsed(started));
                return;
            }
            catch (OperationCanceledException) when (renderToken.IsCancellationRequested)
            {
                logger.LogWarning("Render gave up on {Chars} chars after {ElapsedMs:0} ms (422)",
                    markdown.Length, Elapsed(started));
                await WriteErrorAsync(context, StatusCodes.Status422UnprocessableEntity, "tooComplex",
                    "This document takes too long to render. Try a smaller one.");
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
                logger.LogInformation("Render abandoned: the download stopped after {ElapsedMs:0} ms", Elapsed(started));
                return;
            }

            logger.LogInformation(
                "Rendered {Chars} chars into {Parts} part(s), {OutputChars} chars of JSON; render {RenderMs:0.0} ms, total {ElapsedMs:0.0} ms",
                markdown.Length, messages.Count, outputChars, timings.TotalMs, Elapsed(started));
        }
    }

    /// 429 from the request-count rate limiter (<see cref="RateLimiterOptions.OnRejected"/>).
    public static async ValueTask OnRateLimitedAsync(OnRejectedContext rejected, CancellationToken cancellationToken)
    {
        var context = rejected.HttpContext;
        var limits = context.RequestServices.GetRequiredService<IOptions<WebLimitsOptions>>().Value;
        var seconds = RetryAfterSeconds(rejected.Lease, limits);

        context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LogCategory)
            .LogInformation("Render rejected: rate limit reached (429, retry after {Seconds} s)", seconds);

        await WriteRateLimitedAsync(context, seconds, cancellationToken);
    }

    /// <summary>
    /// The server renders with local resources off, so document content never reaches the file system, and with an
    /// element ceiling the desktop shells don't need: the container has 512 MB, and Core's size-derived budget alone
    /// would let a document at the size limit build a DOM several times that.
    /// </summary>
    internal static RenderContext CreateRenderContext(WebLimitsOptions limits, string? kind = null) =>
        new(RenderKinds.DocumentNameFor(kind), "/")
        {
            AllowLocalResources = false,
            MaxElements = limits.MaxRenderElements > 0 ? limits.MaxRenderElements : null,
        };

    /// Seconds until the window rolls over, from the failed lease; a whole window if the limiter didn't say.
    private static int RetryAfterSeconds(RateLimitLease lease, WebLimitsOptions limits)
    {
        var fallback = limits.RateWindow > TimeSpan.Zero ? limits.RateWindow : TimeSpan.FromMinutes(1);
        var retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) && wait > TimeSpan.Zero
            ? wait
            : fallback;
        return Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
    }

    private static Task WriteRateLimitedAsync(HttpContext context, int seconds, CancellationToken cancellationToken)
    {
        context.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Response.Headers.CacheControl = "no-store";
        return WriteErrorAsync(context, StatusCodes.Status429TooManyRequests, "rateLimited",
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

    /// <summary>The 413, naming the limit the deployment actually runs with rather than a number in a string.</summary>
    internal static string TooLargeMessage(WebLimitsOptions limits)
    {
        var bytes = Math.Max(1, limits.MaxRequestBodyBytes);
        return bytes >= 1024 * 1024
            ? $"This document is over {bytes / (1024 * 1024)} MB."
            : $"This document is over {Math.Max(1, bytes / 1024)} KB.";
    }

    private static Task WriteTooLargeAsync(HttpContext context, WebLimitsOptions limits) =>
        WriteErrorAsync(context, StatusCodes.Status413PayloadTooLarge, "tooLarge", TooLargeMessage(limits));

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
