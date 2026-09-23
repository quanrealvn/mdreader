using System.ComponentModel;
using System.Windows.Input;
using MdReader.Core.Protocol;

namespace MdReader.Shell.ViewModels;

// Implemented by DocumentTabViewModel (WP6); consumed by MainViewModel/TabStrip (WP5).
// ICommand comes from System.ObjectModel, not from WPF, so this contract is framework-free.
public enum DocumentSessionState { Loading, Rendered, Error }

public interface IDocumentTab : INotifyPropertyChanged, IDisposable
{
    int DocId { get; }
    string FilePath { get; }
    string Header { get; }                 // file name (tab text)
    string ToolTip { get; }                // full path
    string Title { get; }                  // rendered title ("" until rendered) → window title
    bool IsActive { get; set; }            // set only by ITabHost
    bool IsDeleted { get; }
    bool IsSplitView { get; set; }         // preview only (false) or Markdown source + preview (true)
    bool IsDirty { get; }                  // the editor has unsaved changes (the tab shows a dot)
    DocumentSessionState State { get; }
    ICommand CloseCommand { get; }         // → ITabHost.Close(this)
    Task SaveAsync();                      // Ctrl+S; no-op when the editor isn't open or isn't dirty
    bool SaveBlocking();                   // same, synchronously (closing a tab or the window)
    Task ReloadAsync();
    void ScrollTo(string fragment);
    void ToggleToc();                      // posts `tocToggle` to this tab's page (final-review S1)
    void ShowFind();
    Task PrintAsync();
    Task ExportPdfAsync();
    Task WhenRenderedAsync(RenderPhase phase, TimeSpan timeout);   // --capture / tests
    Task CapturePreviewAsync(Stream pngDestination);               // --capture
}

/// <summary>
/// The per-shell view of one tab: the control that owns the web view, the find bar and the editor pane. Created by the
/// shell's document host (WPF: <c>DocumentHost</c>), never by the shared layer.
/// </summary>
public interface IDocumentTabView
{
    /// Ctrl+F: show the find bar and focus it.
    void ShowFind();

    /// Ends the find session (tab close).
    void StopFind();

    /// Shows or hides the Markdown editor pane.
    void SetSplitView(bool enabled);

    /// Last step of the tab's Dispose (§4.11): after the find session and the DocumentSession.
    void DisposeWebView();
}
