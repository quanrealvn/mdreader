using System.Text.Json.Serialization;

namespace MdReader.Core.Protocol;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ReadyMessage), "ready")]
[JsonDerivedType(typeof(LinkMessage), "link")]
[JsonDerivedType(typeof(CopyMessage), "copy")]
[JsonDerivedType(typeof(RenderedMessage), "rendered")]
[JsonDerivedType(typeof(TocVisibilityChangedMessage), "tocVisibilityChanged")]
[JsonDerivedType(typeof(TocWidthChangedMessage), "tocWidthChanged")]
[JsonDerivedType(typeof(RetryMessage), "retry")]
[JsonDerivedType(typeof(LogMessage), "log")]
[JsonDerivedType(typeof(PrintModeReadyMessage), "printModeReady")]
[JsonDerivedType(typeof(DropMessage), "drop")]
[JsonDerivedType(typeof(DropTextMessage), "dropText")]
[JsonDerivedType(typeof(TaskToggleMessage), "taskToggle")]
[JsonDerivedType(typeof(SaveFileMessage), "saveFile")]
public abstract record WebMessage;

public sealed record ReadyMessage(int Protocol) : WebMessage;

public sealed record LinkMessage(string Href, bool NewTab) : WebMessage;

public sealed record CopyMessage(string Text) : WebMessage;

public sealed record RenderedMessage(int DocId, int Version, double Ms, RenderPhase Phase) : WebMessage;

public sealed record TocVisibilityChangedMessage(bool Visible) : WebMessage;

/// The user dragged (or keyed) the contents panel to a new width, in CSS px. The page has already applied it.
public sealed record TocWidthChangedMessage(double Width) : WebMessage;

public sealed record RetryMessage : WebMessage;

public sealed record LogMessage(WebLogLevel Level, string Message) : WebMessage;

public sealed record PrintModeReadyMessage(bool Enabled) : WebMessage;

public sealed record DropMessage : WebMessage;   // files arrive in CoreWebView2WebMessageReceivedEventArgs.AdditionalObjects

/// <summary>
/// Something that wasn't a file was dropped on the page — a link dragged out of a browser, or selected text. The page
/// hands over the string and decides nothing: the host validates it (<c>MdReader.Core.Net.DocumentUrl</c>) and either
/// opens an address or says it isn't one.
/// </summary>
public sealed record DropTextMessage(string Text) : WebMessage;

/// The user ticked a task-list checkbox. Line is the 1-based source line the checkbox was rendered from (its data-line,
/// §4.1); Version is the render that box belongs to, so a stale click on an outdated page is ignored.
public sealed record TaskToggleMessage(int Line, bool Checked, int Version) : WebMessage;

/// <summary>
/// The user asked to save something the page produced — today a diagram exported as SVG or PNG. The WebView cancels
/// every download (§8.4) and that stays, so the page hands the bytes to the host instead and the host shows the save
/// dialog. <c>Name</c> is only a suggestion (<c>ExportFileTypes.SuggestFileName</c> rewrites it), <c>MimeType</c> must
/// be one of <c>ExportFileTypes</c>, and <c>Base64</c> carries the content.
/// </summary>
public sealed record SaveFileMessage(string Name, string MimeType, string Base64) : WebMessage;

public enum RenderPhase { [JsonStringEnumMemberName("content")] Content, [JsonStringEnumMemberName("enhanced")] Enhanced }

public enum WebLogLevel { [JsonStringEnumMemberName("debug")] Debug, [JsonStringEnumMemberName("info")] Info,
                          [JsonStringEnumMemberName("warn")] Warn, [JsonStringEnumMemberName("error")] Error }
