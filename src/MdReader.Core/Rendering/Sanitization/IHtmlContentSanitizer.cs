namespace MdReader.Core.Rendering.Sanitization;

/// <summary>
/// Sanitizes rendered HTML (ARCHITECTURE §4.2, §8.2). Deliberately not named <c>IHtmlSanitizer</c> to avoid a clash with
/// <c>Ganss.Xss.IHtmlSanitizer</c>.
/// </summary>
internal interface IHtmlContentSanitizer
{
    SanitizedHtml Sanitize(string html, RenderContext context, CancellationToken cancellationToken);
}

/// <param name="Html">Sanitized HTML; safe to assign to innerHTML.</param>
/// <param name="BlockOffsets">Ascending start offsets (in <paramref name="Html"/>) of every top-level node ([] if empty).</param>
internal sealed record SanitizedHtml(string Html, IReadOnlyList<int> BlockOffsets);
