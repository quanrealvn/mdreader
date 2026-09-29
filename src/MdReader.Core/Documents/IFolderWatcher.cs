namespace MdReader.Core.Documents;

/// <summary>
/// Watches a whole folder tree for files and folders appearing, disappearing or being renamed (ARCHITECTURE M13).
/// Unlike <see cref="IDocumentWatcher"/> it says only "something changed": the file pane answers by re-scanning,
/// which is the only way to learn what the tree looks like now anyway.
/// </summary>
public interface IFolderWatcher : IDisposable
{
    string FolderPath { get; }

    /// <summary>Raised on a thread-pool thread, at most once per debounce window. Consumers marshal to the UI thread.</summary>
    event EventHandler? Changed;
}

public interface IFolderWatcherFactory
{
    IFolderWatcher Create(string folderPath);
}
