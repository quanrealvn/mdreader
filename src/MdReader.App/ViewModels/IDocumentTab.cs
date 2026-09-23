using System.ComponentModel;
using System.IO;
using System.Windows.Input;
using MdReader.Core.Protocol;

namespace MdReader.App.ViewModels;

// Implemented by DocumentTabViewModel (WP6); consumed by MainViewModel/TabStrip (WP5).
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
    bool SaveBlocking();                   // same, synchronously (closing a tab or the window); asks about an overwrite
    bool SaveForSessionEnd();              // logoff/shutdown: save synchronously, never ask
    Task ReloadAsync();
    void ScrollTo(string fragment);
    void ToggleToc();                      // posts `tocToggle` to this tab's page (final-review S1)
    void ShowFind();
    Task PrintAsync();
    Task ExportPdfAsync();
    Task WhenRenderedAsync(RenderPhase phase, TimeSpan timeout);   // --capture / tests
    Task CapturePreviewAsync(Stream pngDestination);               // --capture
}
