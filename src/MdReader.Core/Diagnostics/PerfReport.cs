using System.Text.Json;
using System.Text.Json.Serialization;

namespace MdReader.Core.Diagnostics;

public static class PerfMarks
{
    public const string MainEntered = "mainEntered", WindowShown = "windowShown", WebViewEnvironmentReady = "webViewEnvironmentReady",
        WebViewReady = "webViewReady", PageReady = "pageReady", FirstRenderPosted = "firstRenderPosted",
        FirstRenderedContent = "firstRenderedContent", FirstRenderedEnhanced = "firstRenderedEnhanced";
}

public sealed record PerfDocumentInfo(string Path, long Bytes, double LoadMs, double ParseMs, double HtmlMs,
    double SanitizeMs, int HtmlChars, int Parts, double WebContentMs);

public sealed record PerfReport(int Schema, DateTime ProcessStartUtc, IReadOnlyDictionary<string, double> MarksMs,
    double MaxUiStallMs, int UiStallsOver100Ms, PerfDocumentInfo? Document);   // MarksMs: ms since process start

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, WriteIndented = true)]
[JsonSerializable(typeof(PerfReport))]
public partial class PerfJsonContext : JsonSerializerContext;
