using MdReader.Core.Protocol;
using MdReader.Shell.Documents;

namespace MdReader.Mac;

/// <summary>What arrived from the page, once the origin and the payload have been checked.</summary>
public enum WebMessageKind
{
    /// The origin was wrong, the payload wasn't a string, or it wasn't a valid message: dropped and logged.
    Rejected,

    Ready,

    Drop,

    /// Everything else, which goes on to the session.
    Message,
}

/// <summary>The result of one intake, with the reason when it was rejected.</summary>
public readonly record struct WebMessageIntakeResult(WebMessageKind Kind, WebMessage? Message, string? Error);

/// <summary>
/// The §7.1 rule 7 origin check and the §4.10 payload check for WKWebView, in one place and with no WebKit type in
/// it. On Windows this is the first half of <c>WebView2Channel.OnWebMessageReceived</c>.
/// </summary>
/// <remarks>
/// WebView2 reports the sender as an origin string; a <c>WKScriptMessage</c> reports the frame's request URL, and
/// custom-scheme URLs are not guaranteed to have a tuple origin in WebKit (a non-special scheme's origin is opaque
/// per the URL standard, and WebKit's behaviour there has changed over the years). So the check is made against the
/// frame's <i>URL</i> — which must be the viewer page itself — and against "this is the main frame", both of which
/// are well defined whatever WebKit decides an origin is. That is strictly narrower than the Windows check, which
/// accepts any path on the app origin.
/// </remarks>
public static class WebMessageIntake
{
    /// <param name="frameUrl">The message frame's request URL.</param>
    /// <param name="isMainFrame">Whether the message came from the main frame.</param>
    /// <param name="body">The message body. The page sends JSON text on this transport, so anything else is a
    /// rejection.</param>
    public static WebMessageIntakeResult Classify(string? frameUrl, bool isMainFrame, string? body)
    {
        if (!isMainFrame)
        {
            return new WebMessageIntakeResult(WebMessageKind.Rejected, null, "the message came from a subframe");
        }

        if (!PageOrigin.IsPageUrl(frameUrl))
        {
            return new WebMessageIntakeResult(WebMessageKind.Rejected, null, "the message came from an unexpected URL");
        }

        if (body is null)
        {
            return new WebMessageIntakeResult(WebMessageKind.Rejected, null, "the message body wasn't text");
        }

        if (body.Length > ProtocolConstants.MaxIncomingMessageChars)
        {
            return new WebMessageIntakeResult(WebMessageKind.Rejected, null,
                $"the message is {body.Length} characters, over the {ProtocolConstants.MaxIncomingMessageChars} limit");
        }

        if (!ProtocolSerializer.TryDeserialize(body, out WebMessage? message, out string? error))
        {
            return new WebMessageIntakeResult(WebMessageKind.Rejected, null, error);
        }

        return message switch
        {
            ReadyMessage => new WebMessageIntakeResult(WebMessageKind.Ready, message, null),
            DropMessage => new WebMessageIntakeResult(WebMessageKind.Drop, message, null),
            _ => new WebMessageIntakeResult(WebMessageKind.Message, message, null),
        };
    }

    /// <summary>
    /// The one line of JavaScript that delivers a host message. WebView2 posts the parsed object straight into the
    /// page; <c>evaluateJavaScript</c> takes source, and JSON is already a JavaScript expression, so the payload goes
    /// in as a literal rather than as a string that the page would have to parse a second time.
    /// </summary>
    /// <remarks>
    /// The page defines <c>__mdrHostMessage</c> as a non-writable, non-configurable property in <c>bridge.js</c>
    /// before any document content exists, so content can neither replace it nor shadow it by id (§7.3, "no implicit
    /// globals"). The call is wrapped so a page that has not finished loading throws nothing back into the host.
    /// </remarks>
    public static string DeliveryScript(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return "(function(m){var f=window.__mdrHostMessage;if(typeof f==='function'){f(m);}})(" + json + ")";
    }
}
