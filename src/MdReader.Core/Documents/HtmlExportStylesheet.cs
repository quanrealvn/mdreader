using System.Text;
using MdReader.Core.Paths;

namespace MdReader.Core.Documents;

/// <summary>
/// Flattens the reader's own stylesheet (<c>web/css/app.css</c> and everything it <c>@import</c>s) into one block of
/// CSS with no external references left in it, ready to go inside a single <c>&lt;style&gt;</c> element.
/// </summary>
/// <remarks>
/// <para><b>Why flatten rather than copy the folder.</b> The point of the export is one file somebody can mail or drop
/// on a network share. A <c>.html</c> next to a <c>css/</c> directory stops being one file the moment it is attached to
/// anything, so the stylesheet travels inside the page and the handful of icons it paints with travel inside the
/// stylesheet as <c>data:</c> URIs.</para>
/// <para><b>What is being inlined.</b> These are the application's own files from its own installation folder, not
/// document content — nothing an author writes reaches this code. That is what makes putting the text straight into a
/// <c>&lt;style&gt;</c> element defensible at all. It is still checked: a <c>&lt;style&gt;</c> element ends at the first
/// <c>&lt;/style</c> whatever the CSS meant by it, so an assembled stylesheet containing that sequence is refused
/// outright rather than written out and hoped about.</para>
/// <para><b>Failure is partial, never fatal.</b> A missing or oversized asset leaves its <c>url()</c> exactly as it
/// was: the exported page then has one icon that does not paint, which is a far better outcome than no stylesheet. A
/// missing <c>app.css</c> (a broken installation) gives null, and the caller exports an unstyled page and says so.</para>
/// </remarks>
public static class HtmlExportStylesheet
{
    /// <summary>The stylesheet the reader's page loads, relative to <c>AppPaths.WebRoot</c>.</summary>
    public const string EntryPoint = "css/app.css";

    /// <summary>How deep <c>@import</c> may nest. The real sheet is one level; this is only a stop for a cycle.</summary>
    public const int MaxImportDepth = 8;

    /// <summary>Ceiling on one CSS file. The largest of ours is 18 KB.</summary>
    public const long MaxCssFileBytes = 2L * 1024 * 1024;

    /// <summary>Ceiling on one <c>url()</c> asset. The octicons are under 1 KB; a webfont would be a few hundred.</summary>
    public const long MaxAssetBytes = 1L * 1024 * 1024;

    /// <summary>Ceiling on the assembled sheet, in characters, so a pathological install can't produce a gigabyte
    /// of <c>&lt;style&gt;</c>. Checked between files and between assets rather than mid-write, so the sheet can
    /// overshoot by one of them.</summary>
    public const long MaxTotalChars = 8L * 1024 * 1024;

    private const string StyleEnd = "</style";

    /// <summary>
    /// The reader's stylesheet with every <c>@import</c> expanded and every local <c>url()</c> inlined, or null when
    /// the entry point couldn't be read or the result is unsafe to embed.
    /// </summary>
    /// <param name="webRoot">The application's <c>web</c> folder (<c>AppPaths.WebRoot</c>).</param>
    public static string? Build(string webRoot, IExportFileReader reader, PathPolicy? policy = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(webRoot);
        ArgumentNullException.ThrowIfNull(reader);
        policy ??= PathPolicy.Current;

        var root = policy.NormalizeFullPath(webRoot);
        if (root is null)
        {
            return null;
        }

        var entry = policy.NormalizeFullPath(policy.Combine(root, EntryPoint));
        if (entry is null)
        {
            return null;
        }

        var builder = new StringBuilder(64 * 1024);
        var visited = new HashSet<string>(policy.Comparer);
        var state = new State(reader, policy, root, builder, visited);
        if (!Append(state, entry, depth: 0))
        {
            return null;
        }

        var css = builder.ToString();
        if (css.Length == 0)
        {
            return null;
        }

        // A <style> element's text ends at "</style", whatever CSS syntax says. Our own files never contain it; if
        // one ever did, the page would be a parse error at best and an injection point at worst, so refuse.
        return css.Contains(StyleEnd, StringComparison.OrdinalIgnoreCase) ? null : css;
    }

    private sealed record State(IExportFileReader Reader, PathPolicy Policy, string Root, StringBuilder Output,
                                HashSet<string> Visited);

    /// <summary>Appends one CSS file, expanding its imports and inlining its <c>url()</c>s. False = nothing was read.</summary>
    private static bool Append(State state, string fullPath, int depth)
    {
        if (depth > MaxImportDepth || !state.Visited.Add(fullPath) || state.Output.Length > MaxTotalChars)
        {
            return false;
        }

        if (!state.Policy.IsWithin(fullPath, state.Root))
        {
            return false;   // an @import that climbs out of the web folder names nothing we are willing to embed
        }

        var bytes = state.Reader.TryRead(fullPath, MaxCssFileBytes);
        if (bytes is null)
        {
            return false;
        }

        var text = Decode(bytes);
        var directory = state.Policy.GetDirectoryName(fullPath) ?? state.Root;
        var index = 0;
        while (index < text.Length)
        {
            var at = text.IndexOf("@import", index, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                AppendWithInlinedUrls(state, text.AsSpan(index), directory);
                break;
            }

            AppendWithInlinedUrls(state, text.AsSpan(index, at - index), directory);
            var end = text.IndexOf(';', at);
            if (end < 0)
            {
                break;   // an unterminated @import: everything after it is not CSS we can reason about
            }

            var target = ExtractReference(text.AsSpan(at + "@import".Length, end - at - "@import".Length));
            if (target is not null
                && state.Policy.NormalizeFullPath(state.Policy.Combine(directory, target)) is { } imported)
            {
                state.Output.Append("\n/* ").Append(state.Policy.GetFileName(imported)).Append(" */\n");
                Append(state, imported, depth + 1);
            }

            index = end + 1;
        }

        return true;
    }

    /// <summary>
    /// Copies CSS through, replacing every <c>url(...)</c> that names a file inside the web folder with a
    /// <c>data:</c> URI. Anything else — an absolute URL, an unknown extension, a file that wouldn't read — is copied
    /// verbatim, so the worst case is one rule that doesn't paint.
    /// </summary>
    private static void AppendWithInlinedUrls(State state, ReadOnlySpan<char> css, string directory)
    {
        while (!css.IsEmpty)
        {
            var at = css.IndexOf("url(", StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                state.Output.Append(css);
                return;
            }

            var close = css[at..].IndexOf(')');
            if (close < 0)
            {
                state.Output.Append(css);
                return;
            }

            close += at;
            state.Output.Append(css[..at]);
            var inner = css[(at + "url(".Length)..close];
            var inlined = TryInlineAsset(state, inner, directory);
            if (inlined is null)
            {
                state.Output.Append(css[at..(close + 1)]);
            }
            else
            {
                state.Output.Append("url(\"").Append(inlined).Append("\")");
            }

            css = css[(close + 1)..];
        }
    }

    /// <summary>The <c>data:</c> URI for a stylesheet reference, or null to leave the reference alone.</summary>
    private static string? TryInlineAsset(State state, ReadOnlySpan<char> reference, string directory)
    {
        var target = Unquote(reference);
        if (target.Length == 0
            || target.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("http:", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("https:", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith("//", StringComparison.Ordinal)
            || target.StartsWith('#'))
        {
            return null;
        }

        // Strip a query or fragment ("icon.svg#id"): it names a part of the file, and a data: URI carries the file.
        var cut = target.AsSpan().IndexOfAny('?', '#');
        if (cut >= 0)
        {
            target = target[..cut];
        }

        var mediaType = ExportMediaTypes.ForStyleAsset(System.IO.Path.GetExtension(target.AsSpan()));
        if (mediaType is null || state.Output.Length > MaxTotalChars)
        {
            return null;
        }

        var full = state.Policy.NormalizeFullPath(state.Policy.Combine(directory, Uri.UnescapeDataString(target)));
        if (full is null || state.Policy.IsForbiddenPath(full) || !state.Policy.IsWithin(full, state.Root))
        {
            return null;
        }

        var bytes = state.Reader.TryRead(full, MaxAssetBytes);
        return bytes is null ? null : DataUri.From(mediaType, bytes);
    }

    /// <summary>The target of an <c>@import</c>: <c>"x.css"</c>, <c>'x.css'</c> or <c>url("x.css")</c>.</summary>
    private static string? ExtractReference(ReadOnlySpan<char> specifier)
    {
        var trimmed = specifier.Trim();
        if (trimmed.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            var close = trimmed.IndexOf(')');
            if (close < 0)
            {
                return null;
            }

            trimmed = trimmed[4..close];
        }

        var value = Unquote(trimmed);
        return value.Length == 0 || value.Contains(':') ? null : value;
    }

    private static string Unquote(ReadOnlySpan<char> value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && (trimmed[0] == '"' || trimmed[0] == '\'') && trimmed[^1] == trimmed[0])
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed.Trim().ToString();
    }

    private static string Decode(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
    }
}

/// <summary>Builds <c>data:</c> URIs. Base64 only, so the result can never carry a quote, a space or a tag.</summary>
internal static class DataUri
{
    /// <summary>
    /// <c>data:&lt;mediaType&gt;;base64,&lt;payload&gt;</c>. Both halves come from a fixed alphabet — the media type
    /// from <see cref="ExportMediaTypes"/>, the payload from base64's <c>A-Za-z0-9+/=</c> — so a URI built here cannot
    /// close the attribute it is written into, whatever the bytes were. The image inliner leans on that.
    /// </summary>
    internal static string From(string mediaType, byte[] bytes) =>
        string.Concat("data:", mediaType, ";base64,", Convert.ToBase64String(bytes));
}
