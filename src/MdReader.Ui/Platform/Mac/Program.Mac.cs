using Avalonia;
using MdReader.Core.Diagnostics;
using MdReader.Core.Protocol;
using MdReader.Core.SingleInstance;
using MdReader.Mac;
using MdReader.Shell.ViewModels;
using MdReader.Ui.Platform.Mac;
using MdReader.Ui.Views;

namespace MdReader.Ui;

/// <summary>
/// The macOS half of the startup sequence. The identity and the foreground permission are both Windows answers to a
/// problem macOS doesn't have: LaunchServices keeps the application to one instance per user and activates it
/// itself, so <see cref="MdReader.Core.SingleInstance.OsManagedSingleInstanceChannel"/> never names a mutex or a
/// pipe and nothing has to ask permission to come forward.
/// </summary>
public static partial class Program
{
    /// <summary>
    /// The two virtual hosts move off https, because <c>WKURLSchemeHandler</c> refuses every scheme WebKit
    /// implements itself. This has to happen before anything is rendered: the sanitizer builds each local image's
    /// URL from it, and the page is navigated to it.
    /// </summary>
    private static partial void ConfigurePlatform() => ProtocolConstants.UseScheme(ProtocolConstants.MacScheme);

    private static partial string CurrentUserId() => Environment.UserName;

    private static partial int CurrentSessionId() => 0;

    private static partial void AllowSetForegroundWindow(int processId)
    {
    }

    private static partial void ShowStartupMessage(string title, string message, bool isError) =>
        MacAppKit.ShowMessage(title, message, isError);

    /// <summary>
    /// The menu bar and the open-document messages. The menu bar is not decoration: AppKit offers a key equivalent to
    /// it before the focused view, so it is what makes the §4.12 shortcuts work while focus is inside the page.
    /// </summary>
    private static partial void AttachPlatformShell(MainWindow window, MainViewModel viewModel,
                                                    ISingleInstanceChannel? channel, IAppLog log)
    {
        if (Application.Current is not { } application)
        {
            return;
        }

        MacMenuBar.Attach(application, viewModel, window.ShowAbout, log);
        MacSingleInstance.Attach(application, channel, log);
    }
}
