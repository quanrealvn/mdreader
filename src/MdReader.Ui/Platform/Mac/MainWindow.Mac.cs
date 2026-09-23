namespace MdReader.Ui.Views;

/// <summary>
/// The two lines of the About box that name the platform and its web-view runtime. WebKit has no version to ask for
/// the way the WebView2 Runtime does — it is part of the system and moves with it — so the system's version is what
/// tells a bug report which WebKit this was.
/// </summary>
public partial class MainWindow
{
    private static partial string ProductDescription => "A Markdown reader for macOS.";

    private static partial string GetWebViewVersion() => $"WebKit (macOS {Environment.OSVersion.Version.ToString(3)})";
}
