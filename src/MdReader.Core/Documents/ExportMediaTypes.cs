namespace MdReader.Core.Documents;

/// <summary>
/// File extension → media type, for the things an HTML export inlines as <c>data:</c> URIs.
/// </summary>
/// <remarks>
/// <para>An allow-list, not a lookup with a fallback. A <c>data:</c> URI is the browser's only clue about what it was
/// handed, so guessing (or worse, defaulting to <c>application/octet-stream</c>) produces a file that either shows
/// nothing or asks the browser to sniff. Anything not listed here is left alone by the exporter and counted as an
/// image that didn't travel.</para>
/// <para>SVG is on the image list because the sanitizer's own rule is <c>data:image/*</c> in an <c>img</c> src, and an
/// SVG loaded as an image document is inert: no script runs, no external resource is fetched. That is exactly the
/// exposure the reader already accepts for a local <c>.svg</c> it displays.</para>
/// </remarks>
internal static class ExportMediaTypes
{
    /// <summary>The media type for an image file's extension (with or without the dot), or null.</summary>
    internal static string? ForImage(ReadOnlySpan<char> extension) => Trim(extension) switch
    {
        "png" => "image/png",
        "jpg" or "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "webp" => "image/webp",
        "avif" => "image/avif",
        "bmp" => "image/bmp",
        "svg" => "image/svg+xml",
        "ico" => "image/x-icon",
        _ => null,
    };

    /// <summary>The media type for something a stylesheet's <c>url()</c> may point at: an icon or a font face.</summary>
    internal static string? ForStyleAsset(ReadOnlySpan<char> extension) => Trim(extension) switch
    {
        "woff2" => "font/woff2",
        "woff" => "font/woff",
        "ttf" => "font/ttf",
        "otf" => "font/otf",
        _ => ForImage(extension),
    };

    private static string Trim(ReadOnlySpan<char> extension)
    {
        var span = extension.Length > 0 && extension[0] == '.' ? extension[1..] : extension;
        return span.ToString().ToLowerInvariant();
    }
}
