using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MdReader.Core.Paths;
using MdReader.Core.Protocol;

namespace MdReader.Core.Documents;

/// <summary>
/// All user-facing error and banner wording (ARCHITECTURE §4.4, §9). The error view shows the full path separately,
/// so messages never repeat it: paths inside <c>detail</c> (exception messages usually contain one) are reduced to
/// their file name.
/// </summary>
public static partial class DocumentErrorMessages
{
    /// <summary><see cref="DocumentLoadFailed.Detail"/> for a path that is a folder (<see cref="DocumentLoadError.IoError"/>).</summary>
    internal const string FolderDetail = "The path is a folder, not a file.";

    /// <summary><see cref="DocumentLoadFailed.Detail"/> for <see cref="DocumentLoadError.Binary"/>.</summary>
    internal const string BinaryDetail = "The file contains NUL bytes, so it is not a text file.";

    /// <summary>Longest detail text included in a message or banner; exception messages can be long.</summary>
    internal const int MaxDetailLength = 300;

    /// <summary>Longest raw detail examined; keeps the path-scrubbing passes cheap.</summary>
    private const int MaxRawDetailLength = 4 * MaxDetailLength;

    private const string ShowingLastVersion = "Showing the last loaded version.";
    private const string DeletedText = "The file was deleted or moved. " + ShowingLastVersion;

    private const long OneKB = 1024;
    private const long OneMB = 1024 * 1024;

    public static (string Title, string Message) For(DocumentErrorKind kind, string path, string? detail)
    {
        var cleanDetail = CleanDetail(detail, path);

        return kind switch
        {
            DocumentErrorKind.NotFound => (
                "File not found",
                "It may have been moved, renamed or deleted."),

            DocumentErrorKind.AccessDenied => (
                "Access denied",
                "You don't have permission to read this file."),

            DocumentErrorKind.Locked => (
                "The file is locked",
                "Another program is using this file. Close it, then retry."),

            DocumentErrorKind.Binary => (
                "This doesn't look like a text file",
                "MdReader shows Markdown and other text files, but this file contains binary data."),

            DocumentErrorKind.TooLarge => (
                "File is too large",
                cleanDetail is null
                    ? "This file is larger than MdReader can open."
                    : $"This file is too large to display ({cleanDetail.TrimEnd('.')})."),

            DocumentErrorKind.IoError when string.Equals(detail, FolderDetail, StringComparison.Ordinal) => (
                "This is a folder",
                "MdReader opens Markdown files, not folders. Open a file inside it instead."),

            DocumentErrorKind.IoError => (
                "Couldn't read the file",
                WithDetail("Something went wrong while reading this file.", cleanDetail)),

            DocumentErrorKind.RenderFailed => (
                "Couldn't display this document",
                WithDetail("Something went wrong while displaying it. Details were written to the log.", cleanDetail)),

            _ => (
                "Couldn't open the file",
                WithDetail("Something went wrong.", cleanDetail)),
        };
    }

    /// <summary>
    /// Live-reload failure while content is shown, e.g. Locked → "Couldn't reload: the file is locked. Will retry on
    /// the next change." Warning for Locked/NotFound (usually transient), Error otherwise.
    /// </summary>
    public static BannerInfo ReloadFailedBanner(DocumentErrorKind kind, string? detail)
    {
        var cleanDetail = CleanDetail(detail, path: null);

        return kind switch
        {
            DocumentErrorKind.Locked => new(BannerKind.Warning,
                "Couldn't reload: the file is locked. Will retry on the next change."),

            // Same state as the watcher's Deleted, so the same wording whichever of the two noticed it first.
            DocumentErrorKind.NotFound => DeletedBanner(),

            DocumentErrorKind.AccessDenied => new(BannerKind.Error,
                "Couldn't reload: access denied. " + ShowingLastVersion),

            DocumentErrorKind.Binary => new(BannerKind.Error,
                "Couldn't reload: the file no longer looks like a text file. " + ShowingLastVersion),

            DocumentErrorKind.TooLarge => new(BannerKind.Error,
                (cleanDetail is null
                    ? "Couldn't reload: the file is now too large to display. "
                    : $"Couldn't reload: the file is now too large to display ({cleanDetail.TrimEnd('.')}). ")
                + ShowingLastVersion),

            DocumentErrorKind.RenderFailed => new(BannerKind.Error,
                WithDetail("Couldn't display the latest version.", cleanDetail) + " " + ShowingLastVersion),

            _ => new(BannerKind.Error,
                WithDetail("Couldn't reload the file.", cleanDetail) + " " + ShowingLastVersion),
        };
    }

    /// <summary>Watcher reported Deleted: warning "The file was deleted or moved. Showing the last loaded version."</summary>
    public static BannerInfo DeletedBanner() => new(BannerKind.Warning, DeletedText);

    /// <summary>The file changed on disk while the editor had unsaved changes; neither side is thrown away.</summary>
    public static BannerInfo DiskChangedWhileEditingBanner() => new(BannerKind.Warning,
        "The file changed on disk. Your unsaved changes are still here — press F5 to load the file, or Ctrl+S to overwrite it.");

    /// <summary>Decoder fallback: info "This file isn't valid UTF-8. It is shown as {encodingName}."</summary>
    public static BannerInfo FallbackEncodingBanner(string encodingName)
    {
        var name = CollapseWhitespace(encodingName ?? string.Empty);
        if (name.Length > 40)
        {
            name = name[..40];
        }

        return new(BannerKind.Info, name.Length == 0
            ? "This file isn't valid UTF-8. It is shown in a legacy encoding."
            : $"This file isn't valid UTF-8. It is shown as {name}.");
    }

    /// <summary>
    /// Resource root longer than <c>ResourceRootResolver.MaxMappablePathLength</c>: info "Local images can't be shown
    /// because the folder path is too long."
    /// </summary>
    public static BannerInfo ResourceRootTooLongBanner() =>
        new(BannerKind.Info, "Local images can't be shown because the folder path is too long.");

    /// <summary>
    /// <see cref="DocumentLoadFailed.Detail"/> for <see cref="DocumentLoadError.TooLarge"/>, e.g. "24.3 MB, limit 20 MB".
    /// A size just over the limit is rounded up so it never reads as equal to the limit.
    /// </summary>
    internal static string TooLargeDetail(long length, long maxBytes)
    {
        var limit = FormatSize(maxBytes);
        var size = FormatSize(length);
        if (size == limit)
        {
            size = FormatSize(length, roundUp: true);
        }

        return $"{size}, limit {limit}";
    }

    /// <summary>Formats a byte count with one optional decimal: "1 byte", "512 bytes", "1.5 KB", "24.3 MB".</summary>
    internal static string FormatSize(long bytes, bool roundUp = false)
    {
        if (bytes < OneKB)
        {
            return bytes == 1
                ? "1 byte"
                : bytes.ToString(CultureInfo.InvariantCulture) + " bytes";
        }

        var (unit, name) = bytes < OneMB ? (OneKB, "KB") : (OneMB, "MB");
        var tenths = (double)bytes * 10 / unit;
        var rounded = (roundUp ? Math.Ceiling(tenths) : Math.Round(tenths, MidpointRounding.AwayFromZero)) / 10;
        return rounded.ToString("0.#", CultureInfo.InvariantCulture) + " " + name;
    }

    private static string WithDetail(string lead, string? detail)
    {
        if (detail is null)
        {
            return lead;
        }

        return detail[^1] is '.' or '!' or '?' or '…' ? lead + " " + detail : lead + " " + detail + ".";
    }

    /// <summary>
    /// Makes an exception message presentable: reduces paths to their file name (the view shows the document path),
    /// collapses whitespace and caps the length. Returns null when nothing useful is left.
    /// </summary>
    /// <param name="path">The document path, if known: it is replaced exactly (any casing, raw or full form). Other
    /// absolute paths (drive-letter or UNC, quoted or bare, with spaces or apostrophes) are found heuristically. The
    /// goal is that no directory name survives; losing a few surrounding words in odd cases is acceptable.</param>
    private static string? CleanDetail(string? detail, string? path)
    {
        if (string.IsNullOrWhiteSpace(detail))
        {
            return null;
        }

        // Only a bounded prefix is examined (the result is capped at MaxDetailLength anyway).
        var text = detail.Length > MaxRawDetailLength ? detail[..MaxRawDetailLength] : detail;
        var comparison = PathPolicy.Current.Comparison;
        foreach (var candidate in PathVariants(path))
        {
            var fileName = ShortName(candidate);
            var replacement = fileName.Length > 0 ? fileName : "the file";

            // Win32 errors without a dedicated message are formatted "<reason> : '<path>'"; the reason says it all.
            text = text.Replace(" : '" + candidate + "'", string.Empty, comparison);
            text = text.Replace(candidate, replacement, comparison);
        }

        if (UsesPosixPaths)
        {
            text = PosixQuotedPathSuffix().Replace(text, string.Empty);
            text = ScrubQuotedPaths(text);
            text = PosixBareAbsolutePath().Replace(text, m => ShortNameOrEllipsis(m.Value));
        }
        else
        {
            text = QuotedPathSuffix().Replace(text, string.Empty);
            text = ScrubQuotedPaths(text);
            text = BareAbsolutePath().Replace(text, m => ShortNameOrEllipsis(m.Value));

            // Leftover directory fragments only appear where '\' is a separator; on POSIX the pattern would eat words.
            text = PathFragment().Replace(text, string.Empty);
        }

        text = CollapseWhitespace(text);
        if (text.Length == 0)
        {
            return null;
        }

        if (text.Length > MaxDetailLength)
        {
            text = text[..(MaxDetailLength - 1)].TrimEnd() + "…";
        }

        return text;
    }

    /// <summary>The path as given plus its normalized full form (exception messages use the full path), longest first.</summary>
    private static IEnumerable<string> PathVariants(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        // Lexical only: Path.GetFullPath would expand 8.3 short names and reach the network for a '~' in a UNC path.
        var variants = new List<string> { path };
        if (PathPolicy.Current.NormalizeFullPath(path) is { } full
            && !string.Equals(full, path, PathPolicy.Current.Comparison))
        {
            variants.Add(full);
        }

        return variants.OrderByDescending(v => v.Length);
    }

    private static string ShortName(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path));

    private static string ShortNameOrEllipsis(string path)
    {
        var name = ShortName(path);
        return name.Length > 0 ? name : "…";
    }

    private static string CollapseWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Replaces each quoted absolute path (drive-letter such as C:\ or C:/, or UNC such as \\server\share) with its
    /// quoted file name. See <see cref="FindClosingQuote"/> for apostrophes inside the path.
    /// </summary>
    private static string ScrubQuotedPaths(string text)
    {
        StringBuilder? builder = null;
        var copied = 0;
        var search = 0;
        while (search < text.Length)
        {
            var open = FindQuotedPathStart(text, search);
            if (open < 0)
            {
                break;
            }

            var close = FindClosingQuote(text, open);
            if (close < 0)
            {
                search = open + 1;   // unterminated: left to the bare-path pass
                continue;
            }

            builder ??= new StringBuilder(text.Length);
            builder.Append(text, copied, open - copied)
                .Append(text[open])
                .Append(ShortNameOrEllipsis(text[(open + 1)..close]))
                .Append(text[open]);
            copied = search = close + 1;
        }

        if (builder is null)
        {
            return text;
        }

        builder.Append(text, copied, text.Length - copied);
        return builder.ToString();
    }

    private static int FindQuotedPathStart(string text, int from)
    {
        for (var i = from; i < text.Length - 1; i++)
        {
            if (text[i] is '\'' or '"' && StartsWithAbsolutePath(text.AsSpan(i + 1)))
            {
                return i;
            }
        }

        return -1;
    }

    private static bool StartsWithAbsolutePath(ReadOnlySpan<char> s) =>
        (s.Length >= 3 && char.IsAsciiLetter(s[0]) && s[1] == ':' && s[2] is '\\' or '/')
        || (s.Length >= 2 && ((s[0] == '\\' && s[1] == '\\') || (s[0] == '/' && s[1] == '/')))
        || (UsesPosixPaths && s.Length >= 2 && s[0] == '/');

    /// <summary>True where '/' alone starts an absolute path, so a quoted "/Users/…" is recognised as one.</summary>
    private static bool UsesPosixPaths => PathPolicy.Current.DirectorySeparator == '/';

    /// <summary>
    /// The closing quote is the first quote of the same kind that is followed by the end, whitespace or punctuation,
    /// so an apostrophe inside a name (O'Brien) is skipped. An apostrophe followed by a space ("Chris' Files") also
    /// looks like a close; a later candidate wins when the text in between continues the path (contains a backslash)
    /// and holds no quote of the same kind (which would start the next quoted item).
    /// </summary>
    private static int FindClosingQuote(string text, int open)
    {
        var quote = text[open];
        var chosen = -1;
        for (var k = open + 1; k < text.Length; k++)
        {
            var c = text[k];
            if (c is '\r' or '\n')
            {
                break;
            }

            if (c != quote || (k + 1 < text.Length && !IsClosingDelimiter(text[k + 1])))
            {
                continue;
            }

            if (chosen < 0)
            {
                chosen = k;
                continue;
            }

            var between = text.AsSpan(chosen + 1, k - chosen - 1);
            if (between.Contains('\\') && !between.Contains(quote))
            {
                chosen = k;
            }
            else
            {
                break;
            }
        }

        return chosen;
    }

    private static bool IsClosingDelimiter(char c) =>
        char.IsWhiteSpace(c) || c is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}';

    /// <summary>The " : '&lt;absolute path&gt;'" suffix .NET appends to Win32 messages that have no dedicated text
    /// (always at the end, so it runs to the last quote even if the path contains apostrophes).</summary>
    [GeneratedRegex(@"\s:\s'(?:[A-Za-z]:[\\/]|\\\\|//).*'\s*$")]
    private static partial Regex QuotedPathSuffix();

    /// <summary>
    /// A bare absolute path. Directory segments may contain spaces and apostrophes (every segment must end with a
    /// separator, so the match extends to the last separator before the next drive letter, double quote or line
    /// break); the final segment (the file name) runs to the next whitespace.
    /// </summary>
    [GeneratedRegex(@"(?<![\w:/\\])(?:[A-Za-z]:[\\/]|\\\\)(?:[^\\/"":\r\n]*[\\/])*[^\s\\/""]*")]
    private static partial Regex BareAbsolutePath();

    /// <summary>Leftover directory fragments inside a word ("Files\a.md" → "a.md"), whatever produced them.</summary>
    [GeneratedRegex(@"[^\s'""]+\\")]
    private static partial Regex PathFragment();

    /// <summary>The POSIX form of <see cref="QuotedPathSuffix"/>, where a path starts with a single '/'.</summary>
    [GeneratedRegex(@"\s:\s'/.*'\s*$")]
    private static partial Regex PosixQuotedPathSuffix();

    /// <summary>
    /// A bare POSIX absolute path: a leading '/' that is not preceded by a word character or another '/', so
    /// "and/or" and the "//host" of a URL are left alone. The match runs to the next whitespace.
    /// </summary>
    [GeneratedRegex(@"(?<![\w/])(?:/[^\s/"":\r\n]+)+/?")]
    private static partial Regex PosixBareAbsolutePath();
}
