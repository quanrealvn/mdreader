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
[JsonDerivedType(typeof(TaskToggleMessage), "taskToggle")]
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

/// The user ticked a task-list checkbox. Line is the 1-based source line the checkbox was rendered from (its data-line,
/// §4.1); Version is the render that box belongs to, so a stale click on an outdated page is ignored.
public sealed record TaskToggleMessage(int Line, bool Checked, int Version) : WebMessage;

public enum RenderPhase { [JsonStringEnumMemberName("content")] Content, [JsonStringEnumMemberName("enhanced")] Enhanced }

public enum WebLogLevel { [JsonStringEnumMemberName("debug")] Debug, [JsonStringEnumMemberName("info")] Info,
                          [JsonStringEnumMemberName("warn")] Warn, [JsonStringEnumMemberName("error")] Error }
