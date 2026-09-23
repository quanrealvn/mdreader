using Avalonia.Controls;
using Avalonia.Win32;
using MdReader.Core.Diagnostics;

namespace MdReader.Ui.Services;

/// <summary>
/// Logoff, shutdown and Restart Manager requests (§4.11). WPF surfaces these as <c>Application.SessionEnding</c>;
/// Avalonia has no equivalent, so the main window's WndProc is hooked and WM_QUERYENDSESSION is turned into the same
/// callback. The message is only observed — the default handling still runs, so Windows is never told to wait.
/// </summary>
public sealed class SessionEndWatcher
{
    private const uint WM_QUERYENDSESSION = 0x0011;
    private const string Category = "Startup";

    private readonly IAppLog _log;
    private Action<string>? _onSessionEnding;
    private bool _reported;

    public SessionEndWatcher(IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    /// <param name="onSessionEnding">Runs on the UI thread with the log reason, before the process is allowed to go.</param>
    public void Attach(Window window, Action<string> onSessionEnding)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(onSessionEnding);
        _onSessionEnding = onSessionEnding;
        try
        {
            Win32Properties.AddWndProcHookCallback(window, OnWndProc);
        }
        catch (Exception ex)
        {
            // Only reachable on a non-Win32 backend; unsaved text is then still protected by the close confirmation.
            _log.Write(AppLogLevel.Warning, Category, "Couldn't watch for the Windows session ending.", ex);
        }
    }

    private nint OnWndProc(nint hwnd, uint message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WM_QUERYENDSESSION && !_reported)
        {
            _reported = true;
            _onSessionEnding?.Invoke($"the Windows session is ending (lParam 0x{lParam:X})");
        }

        return 0;
    }
}
