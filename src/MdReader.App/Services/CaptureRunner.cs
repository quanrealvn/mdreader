using System.IO;
using System.Windows;
using System.Windows.Threading;
using MdReader.App.ViewModels;
using MdReader.Core.Cli;
using MdReader.Core.Diagnostics;
using MdReader.Core.Protocol;

namespace MdReader.App.Services;

/// --capture &lt;png&gt; (§4.7): opens the first file, waits until it is rendered with enhancements (≤ 15 s), writes a PNG of the
/// WebView and exits 0. Any failure (no file, load/render error, timeout, write failure) exits 4.
public sealed class CaptureRunner
{
    internal const int ExitCodeSuccess = 0;
    internal const int ExitCodeCaptureFailed = 4;
    internal static readonly TimeSpan RenderTimeout = TimeSpan.FromSeconds(15);
    private const string Category = "Capture";

    private readonly CommandLineOptions _options;
    private readonly ITabHost _tabs;
    private readonly IDocumentOpener _opener;
    private readonly IAppLog _log;

    public CaptureRunner(CommandLineOptions options, ITabHost tabs, IDocumentOpener opener, IAppLog log)
    {
        _options = options;
        _tabs = tabs;
        _opener = opener;
        _log = log;
    }

    public bool IsEnabled => _options.CapturePath is not null;

    /// UI thread, before the main window is shown: opens the document so its load/render starts immediately.
    /// The capture itself runs once the dispatcher loop is running (Program calls this before Application.Run).
    public void Start(Dispatcher dispatcher)
    {
        if (!IsEnabled)
        {
            return;
        }

        IDocumentTab? tab = null;
        Exception? openError = null;
        if (_options.Files.Count == 0)
        {
            openError = new InvalidOperationException("--capture needs a Markdown file to open.");
        }
        else
        {
            try
            {
                _opener.Open(_options.Files[0]);
                tab = _tabs.ActiveTab ?? throw new InvalidOperationException("Opening the document didn't create a tab.");
            }
            catch (Exception ex)
            {
                openError = ex;
            }
        }

        dispatcher.InvokeAsync(() => openError is null ? CaptureAsync(tab!) : FailAsync(openError));
    }

    private async Task CaptureAsync(IDocumentTab tab)
    {
        var path = _options.CapturePath!;
        var temp = path + ".tmp";
        try
        {
            await tab.WhenRenderedAsync(RenderPhase.Enhanced, RenderTimeout);
            if (tab.State != DocumentSessionState.Rendered)
            {
                throw new InvalidOperationException($"The document didn't render (state: {tab.State}).");
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await tab.CapturePreviewAsync(stream);
            }

            if (new FileInfo(temp).Length == 0)
            {
                throw new InvalidOperationException("The captured image is empty.");
            }

            File.Move(temp, path, overwrite: true);
            _log.Write(AppLogLevel.Info, Category, $"Captured {tab.FilePath} to {path}");
            Shutdown(ExitCodeSuccess);
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            await FailAsync(ex);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private Task FailAsync(Exception exception)
    {
        _log.Write(AppLogLevel.Error, Category, $"Capture to {_options.CapturePath} failed", exception);
        Shutdown(ExitCodeCaptureFailed);
        return Task.CompletedTask;
    }

    private static void Shutdown(int exitCode) => Application.Current?.Shutdown(exitCode);
}
