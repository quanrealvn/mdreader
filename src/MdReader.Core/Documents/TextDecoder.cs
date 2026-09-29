using System.Text;
using System.Text.Unicode;

namespace MdReader.Core.Documents;

public readonly record struct TextDecodeResult(bool IsBinary, string Text, string EncodingName, bool UsedFallbackEncoding);

public static class EncodingNames
{
    public const string Utf8 = "UTF-8", Utf8Bom = "UTF-8 with BOM", Utf16LE = "UTF-16 LE", Utf16BE = "UTF-16 BE",
                        Utf32LE = "UTF-32 LE", Utf32BE = "UTF-32 BE", Windows1252 = "Windows-1252";
}

/// <summary>
/// Turns file bytes into text (ARCHITECTURE §4.4). Order: BOM → UTF-16-without-BOM heuristic → NUL ⇒ binary →
/// strict UTF-8 → fallback code page. A leading U+FEFF is stripped from the result.
/// </summary>
public static class TextDecoder
{
    public const int SniffBytes = 8192;

    /// <summary>Code page used when the requested fallback code page is unusable.</summary>
    internal const int DefaultFallbackCodePage = 1252;

    /// <summary>Minimum sniffed length for the UTF-16-without-BOM heuristic.</summary>
    internal const int MinUtf16SniffBytes = 64;

    // All decoders are lenient: invalid sequences become U+FFFD instead of throwing.
    private static readonly Encoding Utf8Lenient = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
    private static readonly Encoding Utf16LELenient = new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: false);
    private static readonly Encoding Utf16BELenient = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: false);
    private static readonly Encoding Utf32LELenient = new UTF32Encoding(bigEndian: false, byteOrderMark: false, throwOnInvalidCharacters: false);
    private static readonly Encoding Utf32BELenient = new UTF32Encoding(bigEndian: true, byteOrderMark: false, throwOnInvalidCharacters: false);

    static TextDecoder()
    {
        // Windows-125x and other ANSI code pages are not built into .NET; the provider ships in the shared framework.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>
    /// True if the bytes start with one of the byte order marks <see cref="Decode"/> recognizes. Saving re-emits exactly
    /// the BOM the file had (<see cref="DocumentTextEncoder"/>); the encoding name alone can't say, because UTF-16/32 are
    /// also detected without one.
    /// </summary>
    public static bool HasByteOrderMark(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])
        || bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])
        || bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])
        || bytes.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]);

    public static TextDecodeResult Decode(ReadOnlySpan<byte> bytes, int fallbackCodePage = 1252)
    {
        // 1. Byte order marks. UTF-32 LE (FF FE 00 00) must be checked before UTF-16 LE (FF FE).
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return Text(Utf8Lenient, bytes[3..], EncodingNames.Utf8Bom);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE, 0x00, 0x00]))
        {
            return Text(Utf32LELenient, bytes[4..], EncodingNames.Utf32LE);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]))
        {
            return Text(Utf32BELenient, bytes[4..], EncodingNames.Utf32BE);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return Text(Utf16LELenient, bytes[2..], EncodingNames.Utf16LE);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return Text(Utf16BELenient, bytes[2..], EncodingNames.Utf16BE);
        }

        var sniff = bytes[..Math.Min(bytes.Length, SniffBytes)];

        // 2. UTF-16 without BOM: ASCII-heavy UTF-16 has a zero byte in every other position.
        switch (DetectUtf16WithoutBom(bytes, sniff))
        {
            case Utf16Guess.LittleEndian:
                return Text(Utf16LELenient, bytes, EncodingNames.Utf16LE);
            case Utf16Guess.BigEndian:
                return Text(Utf16BELenient, bytes, EncodingNames.Utf16BE);
        }

        // 3. NUL bytes are valid UTF-8, but text files don't contain them: treat as binary.
        if (sniff.Contains((byte)0))
        {
            return new TextDecodeResult(IsBinary: true, Text: string.Empty, EncodingName: string.Empty, UsedFallbackEncoding: false);
        }

        // 4. Strict UTF-8.
        if (Utf8.IsValid(bytes))
        {
            return Text(Utf8Lenient, bytes, EncodingNames.Utf8);
        }

        // 5. Legacy code page (the user's ANSI code page, supplied by the caller).
        var (encoding, codePage) = ResolveFallbackEncoding(fallbackCodePage);
        return Text(encoding, bytes, "Windows-" + codePage.ToString(System.Globalization.CultureInfo.InvariantCulture), usedFallback: true);
    }

    /// <summary>
    /// Resolves the fallback code page. Unknown code pages, 0/negative values and Unicode code pages (UTF-7/8/16/32,
    /// e.g. 65001 when Windows' "use UTF-8 for worldwide language support" is on) fall back to Windows-1252: decoding
    /// invalid UTF-8 "as UTF-8" again would only produce replacement characters.
    /// </summary>
    internal static (Encoding Encoding, int CodePage) ResolveFallbackEncoding(int codePage)
    {
        if (codePage > 0 && TryGetLegacyEncoding(codePage) is { } requested)
        {
            return (requested, codePage);
        }

        return (TryGetLegacyEncoding(DefaultFallbackCodePage)!, DefaultFallbackCodePage);
    }

    private static Encoding? TryGetLegacyEncoding(int codePage)
    {
        switch (codePage)
        {
            case 1200 or 1201 or 12000 or 12001 or 65000 or 65001:
                return null;
        }

        try
        {
            return Encoding.GetEncoding(codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static Utf16Guess DetectUtf16WithoutBom(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> sniff)
    {
        // Real UTF-16 files have an even length; SniffBytes is even, so the sniffed window is too.
        if (bytes.Length % 2 != 0 || sniff.Length < MinUtf16SniffBytes)
        {
            return Utf16Guess.None;
        }

        var zeroEven = 0;
        var zeroOdd = 0;
        for (var i = 0; i < sniff.Length; i += 2)
        {
            if (sniff[i] == 0)
            {
                zeroEven++;
            }

            if (sniff[i + 1] == 0)
            {
                zeroOdd++;
            }
        }

        var pairs = sniff.Length / 2.0;
        var ze = zeroEven / pairs;
        var zo = zeroOdd / pairs;

        if (zo >= 0.3 && ze <= 0.05)
        {
            return Utf16Guess.LittleEndian;
        }

        if (ze >= 0.3 && zo <= 0.05)
        {
            return Utf16Guess.BigEndian;
        }

        return Utf16Guess.None;
    }

    private static TextDecodeResult Text(Encoding encoding, ReadOnlySpan<byte> bytes, string encodingName, bool usedFallback = false)
    {
        var text = encoding.GetString(bytes);
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        return new TextDecodeResult(IsBinary: false, Text: text, EncodingName: encodingName, UsedFallbackEncoding: usedFallback);
    }

    private enum Utf16Guess { None, LittleEndian, BigEndian }
}
