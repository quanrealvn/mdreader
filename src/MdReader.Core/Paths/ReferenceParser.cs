namespace MdReader.Core.Paths;

/// <summary>
/// Classifies an author-written URL reference (link href, image src, srcset candidate) lexically, following the
/// rules of ARCHITECTURE §4.3 in order. Never touches the file system or the network.
/// </summary>
/// <remarks>
/// <para>Before step 1 the input is cleaned the way a browser's URL parser does it: leading and trailing C0 controls
/// and spaces are trimmed (a superset of ASCII whitespace), and TAB/LF/CR are removed everywhere. The latter makes
/// <c>"java\tscript:"</c> (what <c>jav&amp;#x09;ascript:</c> decodes to in an attribute) classify as the scheme it
/// would be in a browser, i.e. <see cref="ReferenceKind.OtherScheme"/>.</para>
/// <para><see cref="ParsedReference.DecodedPath"/> is the percent-decoded path (without query and fragment).
/// For local kinds (<see cref="ReferenceKind.Relative"/>, <see cref="ReferenceKind.RootRelative"/>,
/// <see cref="ReferenceKind.WindowsAbsolute"/>, <see cref="ReferenceKind.File"/>, <see cref="ReferenceKind.Unc"/>)
/// '/' is converted to '\'; for <see cref="ReferenceKind.File"/> and a UNC <c>file:</c> URI it is the URI's local path.
/// It is null only for <see cref="ReferenceKind.Empty"/> and <see cref="ReferenceKind.Fragment"/>.</para>
/// </remarks>
public static class ReferenceParser
{
    public static ParsedReference Parse(string? raw)
    {
        var original = raw ?? string.Empty;
        var text = Clean(original);

        // 1. Empty.
        if (text.Length == 0)
        {
            return new ParsedReference(ReferenceKind.Empty, original, null, null, null, null);
        }

        // 2. Fragment-only.
        if (text[0] == '#')
        {
            return new ParsedReference(ReferenceKind.Fragment, original, null, null, Decode(text[1..]), null);
        }

        // 3. Split off #fragment, then ?query; percent-decode the path (UTF-8, invalid sequences stay literal).
        var pathPart = text;
        string? fragment = null;
        var hashIndex = pathPart.IndexOf('#', StringComparison.Ordinal);
        if (hashIndex >= 0)
        {
            fragment = Decode(pathPart[(hashIndex + 1)..]);
            pathPart = pathPart[..hashIndex];
        }

        string? query = null;
        var queryIndex = pathPart.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
        {
            query = pathPart[(queryIndex + 1)..];
            pathPart = pathPart[..queryIndex];
        }

        var decoded = Decode(pathPart);

        // 4. Classify the decoded path.
        if (decoded.Length >= 2 && IsSeparator(decoded[0]) && IsSeparator(decoded[1]))
        {
            // "//host/x", "\\srv\share", and mixed "/\srv" (Windows and browsers treat both as separators).
            return Local(ReferenceKind.Unc, decoded);
        }

        if (decoded.Length >= 2 && IsAsciiLetter(decoded[0]) && decoded[1] == ':')
        {
            return decoded.Length >= 3 && IsSeparator(decoded[2])
                ? Local(ReferenceKind.WindowsAbsolute, decoded)
                : new ParsedReference(ReferenceKind.Invalid, original, decoded, query, fragment, null);   // drive-relative "C:x"
        }

        if (TryGetScheme(decoded, out var scheme))
        {
            return ParseAbsolute(scheme, text, original, decoded, query, fragment);
        }

        return decoded.Length >= 1 && IsSeparator(decoded[0])
            ? Local(ReferenceKind.RootRelative, decoded)
            : Local(ReferenceKind.Relative, decoded);

        ParsedReference Local(ReferenceKind kind, string path) =>
            new(kind, original, ToLocalSeparators(path), query, fragment, null);
    }

    private static ParsedReference ParseAbsolute(
        string scheme, string text, string original, string decoded, string? query, string? fragment)
    {
        var isHttp = scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        var isHttps = scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase);
        var isMailto = scheme.Equals(Uri.UriSchemeMailto, StringComparison.OrdinalIgnoreCase);
        var isFile = scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase);

        if (!isHttp && !isHttps && !isMailto && !isFile)
        {
            return new ParsedReference(ReferenceKind.OtherScheme, original, decoded, query, fragment, null);
        }

        // Parse the (cleaned) raw string, not the decoded one: "http%3A//x" must not become an http URI.
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return new ParsedReference(ReferenceKind.Invalid, original, decoded, query, fragment, null);
        }

        if (isHttp || isHttps)
        {
            return string.IsNullOrEmpty(uri.Host)
                ? new ParsedReference(ReferenceKind.Invalid, original, decoded, query, fragment, null)
                : new ParsedReference(isHttp ? ReferenceKind.Http : ReferenceKind.Https, original, decoded, query, fragment, uri);
        }

        if (isMailto)
        {
            return new ParsedReference(ReferenceKind.Mailto, original, decoded, query, fragment, uri);
        }

        // file: — only drive-absolute local paths and UNC paths are meaningful. Re-check the local path lexically
        // ("file://localhost/C:/x" has IsUnc = true and yields "\\localhost\C:\x", which then counts as UNC).
        var localPath = ToLocalSeparators(uri.LocalPath);
        if (uri.IsUnc || (localPath.Length >= 2 && localPath[0] == '\\' && localPath[1] == '\\'))
        {
            return new ParsedReference(ReferenceKind.Unc, original, localPath, query, fragment, uri);
        }

        return localPath.Length >= 3 && IsAsciiLetter(localPath[0]) && localPath[1] == ':' && localPath[2] == '\\'
            ? new ParsedReference(ReferenceKind.File, original, localPath, query, fragment, uri)
            : new ParsedReference(ReferenceKind.Invalid, original, localPath, query, fragment, null);
    }

    /// <summary>
    /// Trims leading/trailing C0 controls and spaces, and removes TAB, LF and CR everywhere (WHATWG URL parser).
    /// </summary>
    private static string Clean(string value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && value[start] <= ' ')
        {
            start++;
        }

        while (end > start && value[end - 1] <= ' ')
        {
            end--;
        }

        var trimmed = value.AsSpan(start, end - start);
        if (trimmed.IndexOfAny('\t', '\n', '\r') < 0)
        {
            return trimmed.Length == value.Length ? value : trimmed.ToString();
        }

        return string.Create(trimmed.Length - CountTabsAndNewlines(trimmed), value[start..end], static (destination, source) =>
        {
            var index = 0;
            foreach (var c in source)
            {
                if (c is not ('\t' or '\n' or '\r'))
                {
                    destination[index++] = c;
                }
            }
        });
    }

    private static int CountTabsAndNewlines(ReadOnlySpan<char> value)
    {
        var count = 0;
        foreach (var c in value)
        {
            if (c is '\t' or '\n' or '\r')
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Percent-decodes as UTF-8. Invalid or incomplete sequences (e.g. "%E1%BA", "%zz", "%FF") stay literal;
    /// "+" is not a space. (<see cref="Uri.UnescapeDataString(string)"/> has exactly these semantics on .NET Core.)
    /// </summary>
    private static string Decode(string value) =>
        value.Contains('%', StringComparison.Ordinal) ? Uri.UnescapeDataString(value) : value;

    /// <summary><c>^[A-Za-z][A-Za-z0-9+.-]+:</c> — at least two characters, so "C:" is never a scheme.</summary>
    private static bool TryGetScheme(string value, out string scheme)
    {
        scheme = string.Empty;
        if (value.Length < 3 || !IsAsciiLetter(value[0]))
        {
            return false;
        }

        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (c == ':')
            {
                if (i < 2)
                {
                    return false;
                }

                scheme = value[..i];
                return true;
            }

            if (!(IsAsciiLetter(c) || char.IsAsciiDigit(c) || c is '+' or '.' or '-'))
            {
                return false;
            }
        }

        return false;
    }

    private static string ToLocalSeparators(string path) => path.Replace('/', '\\');

    private static bool IsSeparator(char c) => c is '/' or '\\';

    private static bool IsAsciiLetter(char c) => char.IsAsciiLetter(c);
}
