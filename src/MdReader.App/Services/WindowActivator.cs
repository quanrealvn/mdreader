using System.Windows;
using System.Windows.Interop;
using MdReader.App.Interop;

namespace MdReader.App.Services;

/// Brings the main window to the front when a second instance forwards files (§4.11). SetForegroundWindow is permitted
/// because the forwarding process called AllowSetForegroundWindow(our pid) before sending the request.
public sealed class WindowActivator
{
    public void Activate(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (!window.IsVisible)
        {
            window.Show();
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero && NativeMethods.IsIconic(handle))
        {
            // SW_RESTORE returns a minimized window to its previous state (normal or maximized).
            NativeMethods.ShowWindow(handle, NativeMethods.SW_RESTORE);
        }
        else if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
        if (handle != IntPtr.Zero)
        {
            NativeMethods.SetForegroundWindow(handle);
        }
    }
}
