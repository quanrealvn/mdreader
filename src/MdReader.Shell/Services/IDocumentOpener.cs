namespace MdReader.Shell.Services;

// The ONLY entry point for opening documents
// (CLI args, pipe, File→Open, recent files, drag-drop, link clicks).
public interface IDocumentOpener
{
    /// UI thread only. Normalizes with Path.GetFullPath. If a tab with the same path (OrdinalIgnoreCase) exists it is reused:
    /// activated if `activate`, and scrolled to `fragment` if given. Otherwise a new tab is appended. Never throws for bad files
    /// (the tab shows the error view).
    void Open(string path, string? fragment = null, bool activate = true);
}
