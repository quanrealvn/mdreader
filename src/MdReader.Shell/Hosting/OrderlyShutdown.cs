using MdReader.Core.Diagnostics;
using MdReader.Core.Settings;
using MdReader.Shell.Services;
using MdReader.Shell.ViewModels;

namespace MdReader.Shell.Hosting;

/// <summary>
/// The shutdown sequence of §4.11, run once, on the UI thread, before the framework destroys the main window: save
/// unsaved editor text when the Windows session is ending → stop serving forwarded files → record the window placement
/// → freeze the tab session (so closing the tabs below doesn't empty it) → save the settings → close every tab (find →
/// session → web view).
/// </summary>
/// <remarks>
/// Tearing the web views down here matters on logoff/shutdown and Restart Manager requests: once the session is ending
/// the WebView2 controllers become unusable, and the framework's own window teardown would otherwise still call into
/// them (CoreWebView2Controller.set_IsVisible → access violation, settings never saved).
/// </remarks>
public sealed class OrderlyShutdown(Action savePlacement, SettingsCoordinator settings, MainViewModel tabs, SessionService? session,
                                    IAppLog log)
{
    private bool _done;

    /// The stop action returned by <see cref="SingleInstanceGate.Serve"/>, when this instance serves forwarded files.
    public Action? StopForwarding { get; set; }

    /// <param name="reason">Logged, so the log says which way out this was.</param>
    /// <param name="saveUnsavedTabs">
    /// The Windows session is ending: nothing can be asked and the process may be killed in seconds, so unsaved
    /// editor text is written to disk before the tabs close. The normal window close asks first instead (§4.10).
    /// </param>
    public void Run(string reason, bool saveUnsavedTabs = false)
    {
        if (_done)
        {
            return;
        }

        _done = true;
        log.Write(AppLogLevel.Info, "Startup", $"Shutting down: {reason}");
        if (saveUnsavedTabs)
        {
            Step("save the unsaved tabs", tabs.SaveDirtyTabsForSessionEnd);
        }

        Step("stop the pipe server", () => StopForwarding?.Invoke());
        Step("record the window placement", savePlacement);
        Step("record the open tabs", () => session?.Freeze());
        Step("save the settings", settings.Flush);
        Step("close the tabs", tabs.CloseAllTabs);
    }

    private void Step(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            log.Write(AppLogLevel.Error, "Startup", $"Couldn't {what} during shutdown", ex);
        }
    }
}
