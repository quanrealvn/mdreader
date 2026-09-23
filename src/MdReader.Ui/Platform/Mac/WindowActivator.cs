using Avalonia.Controls;
using MdReader.Mac;

namespace MdReader.Ui.Services;

/// <summary>
/// Brings the main window to the front when the system hands this instance files to open (§4.11). On Windows that
/// needs the sending process's permission; on macOS LaunchServices has already activated the application by the time
/// <c>application:openURLs:</c> arrives, and this only makes sure the window is the one in front.
/// </summary>
public sealed class WindowActivator
{
    public void Activate(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        MacAppKit.ActivateApplication();
        window.Activate();
    }
}
