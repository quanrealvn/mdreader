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
    DocumentSessionState State { get; }
    ICommand CloseCommand { get; }         // → ITabHost.Close(this)
    Task ReloadAsync();
    void ScrollTo(string fragment);
    void ToggleToc();                      // posts `tocToggle` to this tab's page (final-review S1)
    void ShowFind();
    Task PrintAsync();
    Task ExportPdfAsync();
    Task WhenRenderedAsync(RenderPhase phase, TimeSpan timeout);   // --capture / tests
    Task CapturePreviewAsync(Stream pngDestination);               // --capture
}
