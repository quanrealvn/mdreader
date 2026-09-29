namespace MdReader.Core.Paths;

public abstract record LinkTarget
{
    private LinkTarget() { }
    public sealed record Anchor(string Fragment) : LinkTarget;                         // decoded; "" = top
    public sealed record MarkdownDocument(string FullPath, string? Fragment) : LinkTarget;

    /// A Markdown document at an http(s) address, linked from a document that itself came from one: MdReader fetches
    /// it into a tab instead of handing it to the browser (§8). Never produced for a document on this machine.
    public sealed record RemoteDocument(Uri Url, string? Fragment) : LinkTarget;

    public sealed record External(Uri Uri) : LinkTarget;                              // http/https only
    public sealed record Blocked(string Reason) : LinkTarget;                         // user-facing sentence
}
