using MdReader.Core.Diagnostics;
using Microsoft.Web.WebView2.Core;

namespace MdReader.App.Services;

public interface IWebViewEnvironmentProvider
{
    void Start();                                             // idempotent; calls CoreWebView2Environment.CreateAsync on the UI thread
    Task<CoreWebView2Environment> GetAsync();                 // same task every time; faults with WebView2RuntimeNotFoundException etc.
}

public interface IExternalLauncher { bool TryOpen(Uri uri); }  // re-validates http/https, then ShellExecute(uri.AbsoluteUri)

public interface IDialogService                                 // every method is a no-op / empty / null in test mode
{
    IReadOnlyList<string> ShowOpenDialog(string? initialDirectory);   // multi-select, MarkdownFileTypes.OpenFileDialogFilter
    string? ShowSavePdfDialog(string suggestedFullPath);
    void ShowError(string title, string message);
}

public interface IClipboardService { bool TrySetText(string text); }  // retries CLIPBRD_E_CANT_OPEN 5×20 ms

public interface IStatusNotifier { void ShowStatus(string message); }  // StatusText toast, 4 s; implemented by MainViewModel

public interface IPerfRecorder                                 // no-op unless --perf-log
{
    bool IsEnabled { get; }
    void Mark(string name);                                    // first call per name wins; PerfMarks constants
    void SetDocument(PerfDocumentInfo info);                   // first document only
}
