namespace MdReader.Core.Rendering;

public interface IMarkdownRenderer
{
    /// Parses, renders and ALWAYS sanitizes. CPU-bound: call from a thread-pool thread.
    /// Thread-safe (pipeline is immutable; per-call HtmlSanitizer instance).
    /// Throws OperationCanceledException (checked between phases). Any other exception is a bug.
    RenderResult Render(string markdown, RenderContext context, CancellationToken cancellationToken = default);
}

/// DocumentPath: absolute, normalized. ResourceRoot: absolute folder mapped to https://doc.mdreader.example/.
public sealed record RenderContext(string DocumentPath, string ResourceRoot)
{
    public string DocumentDirectory => Path.GetDirectoryName(DocumentPath)!;

    /// false (web version, §15): every local image reference (relative, root-relative, Windows absolute, file:, UNC,
    /// author-written doc-host URL) is dropped without any path resolution or file-system call; https and data:image/*
    /// images behave as before. The paths are then not resolved at all (safe on Linux): DocumentPath only supplies the
    /// fallback title (its file name), ResourceRoot is unused.
    public bool AllowLocalResources { get; init; } = true;
}

public sealed record RenderResult(
    string Html,                       // sanitized; safe to assign to innerHTML
    IReadOnlyList<int> BlockOffsets,   // ascending char offsets in Html where each top-level node starts ([] if Html is empty)
    IReadOnlyList<TocEntry> Toc,
    string Title,                      // plain text of first h1, else file name incl. extension ("README.md")
    RenderFeatures Features,
    RenderTimings Timings);

public sealed record TocEntry(int Level, string Id, string Text);   // Id == the id attribute in Html
public sealed record RenderFeatures(bool Mermaid, bool Math, bool Code);
public sealed record RenderTimings(double ParseMs, double HtmlMs, double SanitizeMs, double TotalMs);
