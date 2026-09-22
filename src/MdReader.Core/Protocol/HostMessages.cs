using System.Text.Json.Serialization;
using MdReader.Core.Rendering;
using MdReader.Core.Settings;
using MdReader.Core.Theming;

namespace MdReader.Core.Protocol;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RenderMessage), "render")]
[JsonDerivedType(typeof(RenderPartMessage), "renderPart")]
[JsonDerivedType(typeof(ThemeMessage), "theme")]
[JsonDerivedType(typeof(ScrollToMessage), "scrollTo")]
[JsonDerivedType(typeof(TocVisibilityMessage), "tocVisibility")]
[JsonDerivedType(typeof(BannerMessage), "banner")]
[JsonDerivedType(typeof(ErrorMessage), "error")]
[JsonDerivedType(typeof(PrintModeMessage), "printMode")]
[JsonDerivedType(typeof(TocToggleMessage), "tocToggle")]
[JsonDerivedType(typeof(ReadingStyleMessage), "readingStyle")]
public abstract record HostMessage;

public sealed record TocToggleMessage : HostMessage;   // final-review S1

public sealed record RenderMessage(int DocId, int Version, string Title, string Html, int Parts,
    IReadOnlyList<TocEntry> Toc, RenderFeatures Features, bool PreserveScroll, string? ScrollToId,
    BannerInfo? Banner) : HostMessage;

public sealed record RenderPartMessage(int DocId, int Version, int Index, string Html) : HostMessage;

public sealed record ThemeMessage(AppTheme Theme) : HostMessage;

public sealed record ReadingStyleMessage(ReadingStyle Style) : HostMessage;   // ARCHITECTURE §14

public sealed record ScrollToMessage(string Id) : HostMessage;

public sealed record TocVisibilityMessage(bool Visible) : HostMessage;

public sealed record BannerMessage([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] BannerInfo? Banner) : HostMessage;

public sealed record ErrorMessage(DocumentErrorKind Kind, string Title, string Message, string Path) : HostMessage;

public sealed record PrintModeMessage(bool Enabled) : HostMessage;

public sealed record BannerInfo(BannerKind Kind, string Text);

public enum BannerKind { [JsonStringEnumMemberName("info")] Info, [JsonStringEnumMemberName("warning")] Warning, [JsonStringEnumMemberName("error")] Error }

public enum DocumentErrorKind
{
    [JsonStringEnumMemberName("notFound")] NotFound, [JsonStringEnumMemberName("accessDenied")] AccessDenied,
    [JsonStringEnumMemberName("locked")] Locked, [JsonStringEnumMemberName("binary")] Binary,
    [JsonStringEnumMemberName("tooLarge")] TooLarge, [JsonStringEnumMemberName("ioError")] IoError,
    [JsonStringEnumMemberName("renderFailed")] RenderFailed,
}
