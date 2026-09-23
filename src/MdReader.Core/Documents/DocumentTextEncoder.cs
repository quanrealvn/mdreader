using System.Globalization;
using System.Text;

namespace MdReader.Core.Documents;

/// <summary>
/// Turns edited text back into file bytes with the encoding <see cref="TextDecoder"/> detected (ARCHITECTURE §4.4), so
/// saving a document doesn't silently re-encode it. The byte order mark is re-emitted exactly when the file had one; line
/// endings are whatever the text already contains (nothing normalizes them).
/// </summary>
public static class DocumentTextEncoder
{
    private const string WindowsPrefix = "Windows-";

    static DocumentTextEncoder() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// Encodes <paramref name="text"/> with the encoding named by <see cref="DocumentLoaded.EncodingName"/>. Characters
    /// the encoding can't represent (a legacy code page) become its replacement character rather than an exception. An
    /// unknown name falls back to UTF-8 without a BOM.
    /// </summary>
    public static byte[] Encode(string text, string? encodingName, bool hasBom)
    {
        ArgumentNullException.ThrowIfNull(text);

        var (encoding, bom) = Resolve(encodingName, hasBom);
        var body = encoding.GetBytes(text);
        if (bom.Length == 0)
        {
            return body;
        }

        var bytes = new byte[bom.Length + body.Length];
        bom.CopyTo(bytes, 0);
        body.CopyTo(bytes, bom.Length);
        return bytes;
    }

    /// <summary>The encoding (without a BOM of its own) and the BOM bytes to prepend.</summary>
    internal static (Encoding Encoding, byte[] Bom) Resolve(string? encodingName, bool hasBom)
    {
        switch (encodingName)
        {
            case EncodingNames.Utf8Bom:
                return (Utf8, [0xEF, 0xBB, 0xBF]);
            case EncodingNames.Utf16LE:
                return (Unicode(bigEndian: false), hasBom ? [0xFF, 0xFE] : []);
            case EncodingNames.Utf16BE:
                return (Unicode(bigEndian: true), hasBom ? [0xFE, 0xFF] : []);
            case EncodingNames.Utf32LE:
                return (Utf32(bigEndian: false), hasBom ? [0xFF, 0xFE, 0x00, 0x00] : []);
            case EncodingNames.Utf32BE:
                return (Utf32(bigEndian: true), hasBom ? [0x00, 0x00, 0xFE, 0xFF] : []);
        }

        if (encodingName is not null && encodingName.StartsWith(WindowsPrefix, StringComparison.Ordinal)
            && int.TryParse(encodingName.AsSpan(WindowsPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var codePage)
            && TryGetLegacyEncoding(codePage) is { } legacy)
        {
            return (legacy, []);
        }

        return (Utf8, []);                    // EncodingNames.Utf8 and anything unknown
    }

    private static Encoding Utf8 { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private static Encoding Unicode(bool bigEndian) =>
        new UnicodeEncoding(bigEndian, byteOrderMark: false, throwOnInvalidBytes: false);

    private static Encoding Utf32(bool bigEndian) =>
        new UTF32Encoding(bigEndian, byteOrderMark: false, throwOnInvalidCharacters: false);

    private static Encoding? TryGetLegacyEncoding(int codePage)
    {
        try
        {
            return Encoding.GetEncoding(codePage, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
