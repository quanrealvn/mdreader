namespace MdReader.Core.Paths;

public abstract record LinkTarget
{
    private LinkTarget() { }
    public sealed record Anchor(string Fragment) : LinkTarget;                         // decoded; "" = top
    public sealed record MarkdownDocument(string FullPath, string? Fragment) : LinkTarget;
    public sealed record External(Uri Uri) : LinkTarget;                              // http/https only
    public sealed record Blocked(string Reason) : LinkTarget;                         // user-facing sentence
}
