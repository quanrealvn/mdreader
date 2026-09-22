using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace MdReader.Core.SingleInstance;

/// <summary>
/// Single-instance pipe protocol v1 (ARCHITECTURE §4.5). All integers are little-endian.
/// <list type="number">
/// <item>client connects (<c>InOut</c>, <c>Asynchronous | CurrentUserOnly</c> on both ends)</item>
/// <item>server → client: int32 server PID</item>
/// <item>client: <c>allowSetForegroundWindow(pid)</c></item>
/// <item>client → server: int32 length N (1..65536) + N bytes UTF-8 JSON <c>{"v":1,"files":["C:\\a.md"],"cwd":"C:\\"}</c></item>
/// <item>server → client: 1 byte, 0x01 accepted / 0x00 rejected (bad length, bad version or invalid payload)</item>
/// </list>
/// Everything here works on any <see cref="Stream"/>; timeouts and cancellation come from the caller's token.
/// </summary>
internal static class PipeFraming
{
    public const int Version = 1;
    public const int MaxPayloadBytes = 65_536;
    public const int MaxFiles = 1_000;
    public const int MaxPathChars = 32_767;
    public const byte AckRejected = 0x00;
    public const byte AckAccepted = 0x01;

    private static readonly JsonTypeInfo<PipePayload> PayloadInfo = CreatePayloadInfo();

    public static async ValueTask WriteInt32Async(Stream stream, int value, CancellationToken cancellationToken)
    {
        var buffer = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// Throws <see cref="EndOfStreamException"/> if the stream ends before 4 bytes arrive.
    public static async ValueTask<int> ReadInt32Async(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return BinaryPrimitives.ReadInt32LittleEndian(buffer);
    }

    public static async ValueTask WriteByteAsync(Stream stream, byte value, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(new[] { value }, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// Throws <see cref="EndOfStreamException"/> if the stream has ended.
    public static async ValueTask<byte> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer[0];
    }

    /// Validates the payload members against the v1 rules. Returns null when valid, otherwise the reason.
    /// Files: ≤ 1000 entries, each non-null, fully qualified (absolute), ≤ 32 767 chars, no NUL. Empty list is valid.
    /// Cwd: present, ≤ 32 767 chars, no NUL.
    public static string? Validate(IReadOnlyList<string?>? files, string? cwd)
    {
        if (files is null)
        {
            return "\"files\" is missing.";
        }

        if (files.Count > MaxFiles)
        {
            return $"Too many files ({files.Count}; the limit is {MaxFiles}).";
        }

        for (var index = 0; index < files.Count; index++)
        {
            var file = files[index];
            if (file is null)
            {
                return $"files[{index}] is null.";
            }

            if (file.Length > MaxPathChars)
            {
                return $"files[{index}] is too long ({file.Length} chars; the limit is {MaxPathChars}).";
            }

            if (file.Contains('\0', StringComparison.Ordinal) || !Path.IsPathFullyQualified(file))
            {
                return $"files[{index}] is not an absolute path.";
            }
        }

        if (cwd is null)
        {
            return "\"cwd\" is missing.";
        }

        if (cwd.Length > MaxPathChars || cwd.Contains('\0', StringComparison.Ordinal))
        {
            return "\"cwd\" is invalid.";
        }

        return null;
    }

    /// UTF-8 JSON <c>{"v":1,"files":[…],"cwd":"…"}</c>. No validation and no size check (see <see cref="TryCreateRequestFrame"/>).
    public static byte[] EncodePayload(OpenFilesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return JsonSerializer.SerializeToUtf8Bytes(new PipePayload(Version, request.Files, request.Cwd), PayloadInfo);
    }

    /// Client side: validates the request and builds the whole step-4 frame (int32 length + payload).
    /// Also rejects strings that aren't well-formed UTF-16. NTFS allows unpaired surrogates in names, but
    /// System.Text.Json would silently write them as U+FFFD, so the primary would get (and ack) a path that doesn't
    /// exist. Rejecting makes the secondary open such files standalone instead.
    public static bool TryCreateRequestFrame(OpenFilesRequest request, [NotNullWhen(true)] out byte[]? frame,
                                             [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(request);
        frame = null;
        error = Validate(request.Files, request.Cwd) ?? FindIllFormedUtf16(request.Files, request.Cwd);
        if (error is not null)
        {
            return false;
        }

        var payload = EncodePayload(request);
        if (payload.Length > MaxPayloadBytes)
        {
            error = $"The request is too large ({payload.Length} bytes; the limit is {MaxPayloadBytes}).";
            return false;
        }

        frame = new byte[sizeof(int) + payload.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, payload.Length);
        payload.CopyTo(frame, sizeof(int));
        return true;
    }

    /// Server side: parses and validates a payload. Never throws.
    public static bool TryDecodePayload(ReadOnlySpan<byte> utf8Json, [NotNullWhen(true)] out OpenFilesRequest? request,
                                        [NotNullWhen(false)] out string? error)
    {
        request = null;
        PipePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize(utf8Json, PayloadInfo);
        }
        catch (Exception ex)
        {
            // Untrusted input: JsonException for malformed JSON/UTF-8 or mistyped members; nothing may escape.
            error = $"The payload isn't valid JSON: {ex.Message}";
            return false;
        }

        if (payload is null)
        {
            error = "The payload is null.";
            return false;
        }

        if (payload.V != Version)
        {
            error = payload.V is null ? "\"v\" is missing." : $"Unsupported protocol version {payload.V}.";
            return false;
        }

        error = Validate(payload.Files, payload.Cwd);
        if (error is not null)
        {
            return false;
        }

        request = new OpenFilesRequest([.. payload.Files!.Select(file => file!)], payload.Cwd!);
        return true;
    }

    /// Server side of one connection, steps 2, 4 and 5. <paramref name="acceptRequest"/> runs after a valid payload
    /// and BEFORE the ack; it returns false if the request could not be taken over (→ 0x00).
    /// Throws IOException (incl. EndOfStreamException) if the client disconnects, OperationCanceledException on the token.
    public static async Task<PipeServerOutcome> RunServerExchangeAsync(Stream stream, int serverProcessId,
        Func<OpenFilesRequest, bool> acceptRequest, CancellationToken cancellationToken)
    {
        await WriteInt32Async(stream, serverProcessId, cancellationToken).ConfigureAwait(false);

        var length = await ReadInt32Async(stream, cancellationToken).ConfigureAwait(false);
        if (length is < 1 or > MaxPayloadBytes)
        {
            // Don't read the body: the length can't be trusted. Reject and let the caller close the connection.
            await WriteByteAsync(stream, AckRejected, cancellationToken).ConfigureAwait(false);
            return PipeServerOutcome.Reject($"Invalid payload length {length}.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        if (!TryDecodePayload(payload, out var request, out var error))
        {
            await WriteByteAsync(stream, AckRejected, cancellationToken).ConfigureAwait(false);
            return PipeServerOutcome.Reject(error);
        }

        var accepted = acceptRequest(request);
        await WriteByteAsync(stream, accepted ? AckAccepted : AckRejected, cancellationToken).ConfigureAwait(false);
        return accepted ? PipeServerOutcome.Accept(request) : PipeServerOutcome.Reject("The request handler failed.", request);
    }

    /// Client side of one connection, steps 2–5. <paramref name="frame"/> comes from <see cref="TryCreateRequestFrame"/>.
    /// Ack 0x01 → Forwarded; any other byte → Rejected. Throws IOException (incl. EndOfStreamException) if the server
    /// disconnects, InvalidDataException for a non-positive PID, OperationCanceledException on the token.
    public static async Task<ForwardResult> RunClientExchangeAsync(Stream stream, ReadOnlyMemory<byte> frame,
        Action<int> onServerProcessId, CancellationToken cancellationToken)
    {
        var serverProcessId = await ReadInt32Async(stream, cancellationToken).ConfigureAwait(false);
        if (serverProcessId <= 0)
        {
            // Never pass 0 / ASFW_ANY (-1) on to AllowSetForegroundWindow.
            throw new InvalidDataException($"The server sent an invalid process id ({serverProcessId}).");
        }

        onServerProcessId(serverProcessId);

        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        var ack = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
        return ack == AckAccepted ? ForwardResult.Forwarded : ForwardResult.Rejected;
    }

    /// Reads and discards until the peer closes its end (returns) or the token fires (throws).
    /// The server calls this after the ack so it never closes the pipe before the client has read the ack.
    public static async Task WaitForPeerCloseAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[256];
        while (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
        {
        }
    }

    /// Returns null when every string is well-formed UTF-16, otherwise which one isn't. Call after Validate (no nulls).
    private static string? FindIllFormedUtf16(IReadOnlyList<string> files, string cwd)
    {
        for (var index = 0; index < files.Count; index++)
        {
            if (!IsWellFormedUtf16(files[index]))
            {
                return $"files[{index}] contains an unpaired surrogate and can't be sent as UTF-8 JSON.";
            }
        }

        return IsWellFormedUtf16(cwd) ? null : "\"cwd\" contains an unpaired surrogate and can't be sent as UTF-8 JSON.";
    }

    /// True if every high surrogate is immediately followed by a low surrogate and no low surrogate stands alone.
    internal static bool IsWellFormedUtf16(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var c = value[index];
            if (char.IsHighSurrogate(c) && index + 1 < value.Length && char.IsLowSurrogate(value[index + 1]))
            {
                index++;   // a valid pair
            }
            else if (char.IsSurrogate(c))
            {
                return false;
            }
        }

        return true;
    }

    private static JsonTypeInfo<PipePayload> CreatePayloadInfo()
    {
        // Same source-generated metadata; relaxed escaping keeps non-ASCII paths at their UTF-8 size.
        var options = new JsonSerializerOptions(PipeJsonContext.Default.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        options.MakeReadOnly();
        return (JsonTypeInfo<PipePayload>)options.GetTypeInfo(typeof(PipePayload));
    }
}

/// Result of <see cref="PipeFraming.RunServerExchangeAsync"/>: the ack that was sent and why.
internal sealed record PipeServerOutcome(bool Accepted, OpenFilesRequest? Request, string? RejectReason)
{
    public static PipeServerOutcome Accept(OpenFilesRequest request) => new(true, request, null);
    public static PipeServerOutcome Reject(string reason, OpenFilesRequest? request = null) => new(false, request, reason);
}

/// Wire shape of the step-4 payload. Every member is nullable so that missing members are detected by validation
/// instead of silently defaulting.
internal sealed record PipePayload(
    [property: JsonPropertyName("v")] int? V,
    [property: JsonPropertyName("files")] IReadOnlyList<string?>? Files,
    [property: JsonPropertyName("cwd")] string? Cwd);

/// Default (not Web) options on purpose: case-sensitive names and no numbers-as-strings, so "v":"1" is rejected.
[JsonSourceGenerationOptions]
[JsonSerializable(typeof(PipePayload))]
internal partial class PipeJsonContext : JsonSerializerContext;
