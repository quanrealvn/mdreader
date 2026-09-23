namespace MdReader.Shell.Services;

/// <summary>
/// The running application as the shared shell sees it: how to stop it, how to tell the user something went wrong, and
/// how to get back onto the UI thread. Implemented per shell (WPF: <c>Application.Current</c> + <c>MessageBox</c>;
/// Avalonia: the classic desktop lifetime, whose shutdown must be deferred because tearing the lifetime down from
/// inside a window's attach pulls the visual tree apart underneath Avalonia).
/// </summary>
public interface IAppHost
{
    /// <summary>Ends the process with <paramref name="exitCode"/>. Safe to call from any thread; may be deferred.</summary>
    void Shutdown(int exitCode);

    /// <summary>Shows a modal error to the user. No-op in test mode (§4.7).</summary>
    void ShowError(string title, string message);

    /// <summary>
    /// Asks the user a yes/no question about a failure (only the WebView2 runtime download prompt today).
    /// False in test mode, so automated runs never block on a dialog.
    /// </summary>
    bool ConfirmError(string title, string message);

    /// <summary>Queues <paramref name="action"/> on the UI thread.</summary>
    void Post(Action action);
}
