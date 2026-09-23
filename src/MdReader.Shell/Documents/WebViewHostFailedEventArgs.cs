using MdReader.Core.Protocol;

namespace MdReader.Shell.Documents;

/// <summary>Why a tab can't show its page at all (the shell shows its own error in place of the web view).</summary>
/// <param name="requiresNewWebView">The web view is gone for good (on Windows: the browser process exited), so
/// re-navigating can't help and only a new web view can.</param>
public sealed class WebViewHostFailedEventArgs(DocumentErrorKind kind, string detail, bool requiresNewWebView) : EventArgs
{
    public DocumentErrorKind Kind { get; } = kind;

    public string Detail { get; } = detail;

    public bool RequiresNewWebView { get; } = requiresNewWebView;
}
