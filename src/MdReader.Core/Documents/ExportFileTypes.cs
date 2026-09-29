using System.Globalization;
using System.Text;

namespace MdReader.Core.Documents;

/// <summary>A file the page can ask the host to save, and what the save dialog should look like for it.</summary>
public sealed record ExportFileType(string MediaType, string Extension, string DialogTitle, string DialogFilter);

/// <summary>
/// The allow-list behind the <c>saveFile</c> web message (ARCHITECTURE §7.2): the page hands the host bytes and a media
/// type, and only the types below ever reach a save dialog.
/// </summary>
/// <remarks>
/// The page is app code, but the document it renders is not, so the suggested file name is treated as untrusted:
/// <see cref="SuggestFileName"/> keeps letters, digits and a few separators and nothing else, so no directory, drive,
/// alternate data stream or device name can be smuggled into the dialog's starting point.
/// </remarks>
public static class ExportFileTypes
{
    /// <summary>The name used when nothing usable was suggested.</summary>
    public const string FallbackName = "diagram";

    /// <summary>Longest suggested stem; long enough for a heading, short enough to stay inside MAX_PATH with a folder.</summary>
    public const int MaxNameLength = 80;

    private static readonly ExportFileType Svg =
        new("image/svg+xml", "svg", "Save diagram as SVG", "SVG image (*.svg)|*.svg");

    private static readonly ExportFileType Png =
        new("image/png", "png", "Save diagram as PNG", "PNG image (*.png)|*.png");

    /// <summary>Windows reserves these stems on every extension.</summary>
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>The export type for <paramref name="mediaType"/>, or null when the page asked for something else.</summary>
    public static ExportFileType? Find(string? mediaType) => mediaType switch
    {
        "image/svg+xml" => Svg,
        "image/png" => Png,
        _ => null,
    };

    /// <summary>
    /// A safe file name (stem + the type's extension) from whatever the page suggested. Never empty, never a path,
    /// never a reserved device name.
    /// </summary>
    public static string SuggestFileName(string? requested, ExportFileType type)
    {
        ArgumentNullException.ThrowIfNull(type);

        var stem = Sanitize(requested, type.Extension);
        return string.Create(CultureInfo.InvariantCulture, $"{stem}.{type.Extension}");
    }

    /// <summary>A combining mark, which decomposed Vietnamese and Indic text needs to stay readable.</summary>
    private static bool IsCombiningMark(char c) => char.GetUnicodeCategory(c) is
        UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;

    private static string Sanitize(string? requested, string extension)
    {
        if (string.IsNullOrEmpty(requested))
        {
            return FallbackName;
        }

        var builder = new StringBuilder(Math.Min(requested.Length, MaxNameLength));
        var lastWasSeparator = false;
        foreach (var c in requested)
        {
            if (builder.Length >= MaxNameLength)
            {
                break;
            }

            // Letters, digits and marks from any script (a Vietnamese or Japanese heading makes a fine file name), plus
            // the three separators people expect. Everything else — including '.', so no second extension and no
            // trailing dot — collapses into a single '-'.
            if (char.IsLetterOrDigit(c) || IsCombiningMark(c))
            {
                builder.Append(c);
                lastWasSeparator = false;
                continue;
            }

            if (lastWasSeparator || builder.Length == 0)
            {
                continue;
            }

            builder.Append(c is '_' or '-' or ' ' ? c : '-');
            lastWasSeparator = true;
        }

        while (builder.Length > 0 && builder[^1] is '-' or '_' or ' ')
        {
            builder.Length--;
        }

        var stem = builder.ToString();
        if (stem.Length == 0)
        {
            return FallbackName;
        }

        foreach (var reserved in ReservedNames)
        {
            if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase))
            {
                return string.Create(CultureInfo.InvariantCulture, $"{stem}-{extension}");
            }
        }

        return stem;
    }
}
