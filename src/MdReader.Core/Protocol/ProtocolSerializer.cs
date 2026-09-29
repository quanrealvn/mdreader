using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using MdReader.Core.Rendering;

namespace MdReader.Core.Protocol;

public static class ProtocolSerializer
{
    // A copy of the source-generated ProtocolJsonContext options: the copy keeps the context as its TypeInfoResolver, so
    // all metadata stays source-generated. Only the encoder and the number handling differ (see CreateOptions).
    private static readonly JsonSerializerOptions Options = CreateOptions();
    private static readonly JsonTypeInfo<HostMessage> HostMessageInfo = (JsonTypeInfo<HostMessage>)Options.GetTypeInfo(typeof(HostMessage));
    private static readonly JsonTypeInfo<WebMessage> WebMessageInfo = (JsonTypeInfo<WebMessage>)Options.GetTypeInfo(typeof(WebMessage));

    /// Uses JavaScriptEncoder.UnsafeRelaxedJsonEscaping (safe: never embedded in HTML; 30% smaller than default).
    /// ALWAYS serializes with static type HostMessage (else the "type" discriminator is omitted [Verified]).
    public static string Serialize(HostMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return JsonSerializer.Serialize(message, HostMessageInfo);
    }

    /// Splits Html at BlockOffsets into parts of ≤ maxPartChars (a single oversized block is its own part);
    /// returns [render(parts=n, html=part0), renderPart(1..n-1)]. Empty Html → one part "".
    public static IReadOnlyList<string> SerializeRender(int docId, int version, RenderResult result, bool preserveScroll,
        string? scrollToId, BannerInfo? banner, int maxPartChars = ProtocolConstants.MaxPartChars)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPartChars);

        var parts = SplitHtml(result.Html, result.BlockOffsets, maxPartChars);
        var messages = new string[parts.Count];
        messages[0] = Serialize(new RenderMessage(docId, version, result.Title, parts[0], parts.Count, result.Toc,
            result.Features, preserveScroll, scrollToId, banner));
        for (var index = 1; index < parts.Count; index++)
        {
            messages[index] = Serialize(new RenderPartMessage(docId, version, index, parts[index]));
        }

        return messages;
    }

    /// Never throws. Rejects > MaxIncomingMessageChars, unknown "type", missing/mistyped required members.
    public static bool TryDeserialize(string json, [NotNullWhen(true)] out WebMessage? message, out string? error)
    {
        message = null;
        if (json is null)
        {
            error = "The message is null.";
            return false;
        }

        if (json.Length > ProtocolConstants.MaxIncomingMessageChars)
        {
            error = $"The message is too large ({json.Length} chars; the limit is {ProtocolConstants.MaxIncomingMessageChars}).";
            return false;
        }

        WebMessage? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(json, WebMessageInfo);
        }
        catch (Exception ex)
        {
            // JsonException: invalid JSON, unknown "type", missing/mistyped/null required member.
            // NotSupportedException: no "type" at all (WebMessage is abstract). Anything else is caught too: never throws.
            error = $"Invalid web message: {ex.Message}";
            return false;
        }

        if (parsed is null)
        {
            error = "Invalid web message: the JSON value is null.";
            return false;
        }

        // The string enum converter also accepts integers; an integer outside the enum is a mistyped member.
        var undefinedEnumMember = parsed switch
        {
            RenderedMessage { Phase: var phase } when !Enum.IsDefined(phase) => "phase",
            LogMessage { Level: var level } when !Enum.IsDefined(level) => "level",
            _ => null,
        };
        if (undefinedEnumMember is not null)
        {
            error = $"Invalid web message: \"{undefinedEnumMember}\" has an unknown value.";
            return false;
        }

        message = parsed;
        error = null;
        return true;
    }

    /// Splits <paramref name="html"/> into consecutive parts whose concatenation is exactly <paramref name="html"/>.
    /// Cuts only at block offsets. Consecutive blocks are packed greedily while a part stays ≤ maxPartChars; a single
    /// block longer than maxPartChars is a part of its own. Offsets that are ≤ 0, ≥ html.Length or not strictly
    /// ascending are ignored. Always returns at least one part (empty html → [""]).
    internal static IReadOnlyList<string> SplitHtml(string html, IReadOnlyList<int> blockOffsets, int maxPartChars)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(blockOffsets);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPartChars);

        if (html.Length <= maxPartChars)
        {
            return [html];
        }

        var parts = new List<string>();
        var start = 0;   // start of the current, not yet emitted part
        var end = 0;     // furthest block boundary accepted into the current part (always the previous cut)

        void AcceptBoundary(int cut)
        {
            // [end, cut) is exactly one block.
            if (cut - start > maxPartChars && end > start)
            {
                parts.Add(html[start..end]);
                start = end;
            }

            end = cut;
            if (end - start > maxPartChars)
            {
                parts.Add(html[start..end]);   // a single oversized block is its own part
                start = end;
            }
        }

        foreach (var offset in blockOffsets)
        {
            if (offset > end && offset < html.Length)
            {
                AcceptBoundary(offset);
            }
        }

        AcceptBoundary(html.Length);
        if (start < html.Length)
        {
            parts.Add(html[start..]);
        }

        return parts;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(ProtocolJsonContext.Default.Options)
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            // JsonSerializerDefaults.Web reads numbers from strings; a web message with "docId":"1" is mistyped, so
            // reject it. Writing is unaffected (Web never writes numbers as strings).
            NumberHandling = JsonNumberHandling.Strict,
        };
        options.MakeReadOnly();
        return options;
    }
}
