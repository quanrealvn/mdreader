using System.Diagnostics;
using System.Security.Principal;
using MdReader.Core.Diagnostics;
using MdReader.Core.SingleInstance;
using MdReader.Shell.ViewModels;
using MdReader.Ui.Interop;
using MdReader.Ui.Views;

namespace MdReader.Ui;

/// <summary>
/// The Windows half of the startup sequence: the identity the single-instance mutex and pipe are named after, the
/// permission a second launch grants the running one, and the message box that has to work before Avalonia exists.
/// </summary>
public static partial class Program
{
    /// Nothing: WebView2's virtual host mapping serves the two hosts over https, which is the default.
    private static partial void ConfigurePlatform()
    {
    }

    private static partial string CurrentUserId()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!.Value;
    }

    private static partial int CurrentSessionId()
    {
        using var process = Process.GetCurrentProcess();
        return process.SessionId;
    }

    private static partial void AllowSetForegroundWindow(int processId) =>
        NativeMethods.AllowSetForegroundWindow(processId);

    private static partial void ShowStartupMessage(string title, string message, bool isError) =>
        Win32Dialogs.Show(0, title, message, NativeMethods.MB_OK,
                          isError ? NativeMethods.MB_ICONWARNING : NativeMethods.MB_ICONINFORMATION);

    /// Nothing: Windows has no menu bar of its own to fill, and a second instance forwards down a named pipe.
    private static partial void AttachPlatformShell(MainWindow window, MainViewModel viewModel,
                                                    ISingleInstanceChannel? channel, IAppLog log)
    {
    }
}
