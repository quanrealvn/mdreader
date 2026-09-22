namespace MdReader.Core.Documents;

public interface IDocumentWatcher : IDisposable
{
    string DocumentPath { get; }
    /// Raised on a thread-pool thread, at most once per debounce window. Consumers marshal to the UI thread.
    event EventHandler<DocumentChangedEventArgs>? Changed;
}

public interface IDocumentWatcherFactory { IDocumentWatcher Create(string documentPath); }

public enum DocumentChangeKind { Changed, Deleted }

public sealed class DocumentChangedEventArgs(DocumentChangeKind kind) : EventArgs
{
    public DocumentChangeKind Kind { get; } = kind;
}
