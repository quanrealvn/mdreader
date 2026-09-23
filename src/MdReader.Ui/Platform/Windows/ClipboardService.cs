using System.Runtime.InteropServices;
using MdReader.Core.Diagnostics;
using MdReader.Shell.Services;
using MdReader.Ui.Interop;

namespace MdReader.Ui.Services;

/// <summary>
/// Clipboard writes with a short retry when another process holds the clipboard open (the Win32 clipboard is a
/// single-owner resource, so <c>OpenClipboard</c> fails with ERROR_ACCESS_DENIED). UI thread only.
/// </summary>
/// <remarks>
/// Avalonia's <c>IClipboard</c> is asynchronous and the shared shell asks for a synchronous answer (a paste that
/// silently happened "later" would be worse than one that says it failed), so this goes straight to user32, which is
/// where the WPF shell's <c>Clipboard.SetDataObject</c> ends up as well.
/// </remarks>
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
            if (TryWrite(text, out bool busy))
            {
                return true;
            }

            if (!busy)
            {
                return false;
            }

            Thread.Sleep(RetryDelayMs);
        }

        return false;
    }

    private bool TryWrite(string text, out bool busy)
    {
        busy = false;
        if (!NativeMethods.OpenClipboard(AvaloniaAppHost.GetOwnerHandle()))
        {
            busy = Marshal.GetLastWin32Error() == NativeMethods.ERROR_ACCESS_DENIED;
            return false;
        }

        nint memory = 0;
        try
        {
            if (!NativeMethods.EmptyClipboard())
            {
                return false;
            }

            nuint bytes = (nuint)((text.Length + 1) * sizeof(char));
            memory = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, bytes);
            if (memory == 0)
            {
                _log.Write(AppLogLevel.Info, "Clipboard", $"GlobalAlloc({bytes}) failed.");
                return false;
            }

            nint target = NativeMethods.GlobalLock(memory);
            if (target == 0)
            {
                return false;
            }

            try
            {
                unsafe
                {
                    var span = new Span<char>((void*)target, text.Length + 1);
                    text.CopyTo(span);
                    span[text.Length] = '\0';
                }
            }
            finally
            {
                NativeMethods.GlobalUnlock(memory);
            }

            if (NativeMethods.SetClipboardData(NativeMethods.CF_UNICODETEXT, memory) == 0)
            {
                _log.Write(AppLogLevel.Info, "Clipboard", $"SetClipboardData failed (Win32 error {Marshal.GetLastWin32Error()}).");
                return false;
            }

            memory = 0;   // the clipboard owns the block now
            return true;
        }
        finally
        {
            if (memory != 0)
            {
                NativeMethods.GlobalFree(memory);
            }

            NativeMethods.CloseClipboard();
        }
    }
}
