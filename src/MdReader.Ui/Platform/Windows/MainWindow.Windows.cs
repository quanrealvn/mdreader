using Microsoft.Web.WebView2.Core;

namespace MdReader.Ui.Views;

/// <summary>The two lines of the About box that name the platform and its web-view runtime.</summary>
public partial class MainWindow
{
    private static partial string ProductDescription => "A Markdown reader for Windows.";

    private static partial string GetWebViewVersion()
    {
        try
        {
            return "WebView2 Runtime " + (CoreWebView2Environment.GetAvailableBrowserVersionString() ?? "not installed");
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException)
        {
            return "WebView2 Runtime not installed";
        }
    }
}
