using MdReader.Core.Diagnostics;

namespace MdReader.Shell.Services;

/// <summary>
/// The shell's native web-view environment: WebView2's <c>CoreWebView2Environment</c> on Windows, nothing at all on
/// WKWebView. The shared layer only needs to start it early (§6) and know when it is usable; the concrete environment
/// is handed out by the backend's own interface (<c>MdReader.Edge.IWebView2EnvironmentProvider</c>).
/// </summary>
public interface IWebViewEnvironmentProvider
{
    /// Idempotent; called on the UI thread as early as possible.
    void Start();

    /// The same task every time: completes when the environment is usable, faults with the platform's error.
    Task WhenReadyAsync();
}

public interface IExternalLauncher { bool TryOpen(Uri uri); }  // re-validates http/https, then ShellExecute(uri.AbsoluteUri)

public interface IDialogService                                 // every method is a no-op / empty / null in test mode
{
    IReadOnlyList<string> ShowOpenDialog(string? initialDirectory);   // multi-select, MarkdownFileTypes.OpenFileDialogFilter
    string? ShowSavePdfDialog(string suggestedFullPath);
    void ShowError(string title, string message);

    /// "Save changes to <name>?" before a dirty tab (or the window) closes. Test mode: Save, with no UI and no blocking.
    SaveChangesChoice ConfirmSaveChanges(string fileName);

    /// The file changed on disk while the editor was dirty and the user asked to save anyway. Test mode: true.
    bool ConfirmOverwriteChangedFile(string fileName);
}

/// What the user answered to "Save changes to …?".
public enum SaveChangesChoice { Save, DontSave, Cancel }

public interface IClipboardService { bool TrySetText(string text); }  // retries CLIPBRD_E_CANT_OPEN 5x20 ms

public interface IStatusNotifier { void ShowStatus(string message); }  // StatusText toast, 4 s; implemented by MainViewModel

public interface IPerfRecorder                                 // no-op unless --perf-log
{
    bool IsEnabled { get; }
    void Mark(string name);                                    // first call per name wins; PerfMarks constants
    void SetDocument(PerfDocumentInfo info);                   // first document only
}
