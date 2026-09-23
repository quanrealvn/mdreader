using System.Runtime.InteropServices;
using System.Windows;
using MdReader.App.Interop;
using MdReader.Core.Diagnostics;
using MdReader.Shell.Services;

namespace MdReader.App.Services;

/// Clipboard writes with a short retry when another process holds the clipboard open (CLIPBRD_E_CANT_OPEN). UI thread (STA) only.
public sealed class ClipboardService : IClipboardService
{
    private const int Attempts = 5;
    private const int RetryDelayMs = 20;

    private readonly IAppLog _log;

    public ClipboardService(IAppLog log)
    {
        _log = log;
    }

    public bool TrySetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (COMException ex) when (ex.HResult == NativeMethods.CLIPBRD_E_CANT_OPEN && attempt < Attempts)
            {
                Thread.Sleep(RetryDelayMs);
            }
            catch (ExternalException ex)
            {
                _log.Write(AppLogLevel.Info, "Clipboard", $"Couldn't set clipboard text (attempt {attempt})", ex);
                return false;
            }
        }

        return false;
    }
}
