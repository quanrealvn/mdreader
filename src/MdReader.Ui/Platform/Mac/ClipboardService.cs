using MdReader.Core.Diagnostics;
using MdReader.Mac;
using MdReader.Shell.Services;

namespace MdReader.Ui.Services;

/// <summary>
/// Clipboard writes through the general <c>NSPasteboard</c>. Unlike the Win32 clipboard, which one process can hold
/// open and lock everyone else out of, the pasteboard is never busy: a write either takes or reports that it didn't,
/// so there is nothing to retry here.
/// </summary>
public sealed class ClipboardService : IClipboardService
{
    private const string Category = "Clipboard";

    private readonly IAppLog _log;

    public ClipboardService(IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    public bool TrySetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            if (MacAppKit.TrySetClipboardText(text))
            {
                return true;
            }

            _log.Write(AppLogLevel.Info, Category, "The pasteboard refused the text.");
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Warning, Category, "Writing to the pasteboard failed.", ex);
        }

        return false;
    }
}
