using MdReader.Core.Protocol;

namespace MdReader.App.Documents;

// Boundary between DocumentSession (logic) and WebViewBridge (WebView2).
public interface IWebViewChannel
{
    bool IsReady { get; }                                     // 'ready' received for the current page load
    event EventHandler? Ready;                                // every (re)load, UI thread
    event EventHandler<WebMessage>? MessageReceived;          // validated messages except 'ready' and 'drop', UI thread
    event EventHandler<IReadOnlyList<string>>? FilesDropped;  // from 'drop' + AdditionalObjects, or file: navigation
    void Post(string json);                                   // UI thread; if !IsReady: logged and dropped
    Microsoft.Web.WebView2.Core.CoreWebView2? Core { get; }   // for Find, print, capture, zoom
}
