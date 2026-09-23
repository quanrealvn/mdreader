using Avalonia.Controls;
using MdReader.Core.Diagnostics;

namespace MdReader.Ui.Services;

/// <summary>
/// The macOS counterpart of the Windows session-end watcher (§4.11). There is nothing to hook: a log-out or restart
/// sends the application <c>applicationShouldTerminate:</c>, and Avalonia answers it by closing the main window,
/// which already runs the orderly shutdown through <c>Window.Closing</c>. Unsaved text is protected by the same
/// close confirmation either way.
/// </summary>
/// <remarks>
/// The class stays so the composition root and the startup sequence read identically on both platforms; what it
/// would hook simply doesn't exist here.
/// </remarks>
public sealed class SessionEndWatcher
{
    private const string Category = "Startup";

    private readonly IAppLog _log;

    public SessionEndWatcher(IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    public void Attach(Window window, Action<string> onSessionEnding)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(onSessionEnding);
        _log.Write(AppLogLevel.Debug, Category,
            "macOS ends a session by closing the window, which already runs the orderly shutdown; nothing to watch.");
    }
}
