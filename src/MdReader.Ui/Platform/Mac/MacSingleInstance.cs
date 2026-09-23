using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using MdReader.Core.Diagnostics;
using MdReader.Core.SingleInstance;
using MdReader.Mac;

namespace MdReader.Ui.Platform.Mac;

/// <summary>
/// The macOS side of single instance (ARCHITECTURE §4.5): LaunchServices never starts a second copy of a bundled
/// application. It activates the running one and hands it the files, which arrive as application-delegate messages
/// and are delivered here to <see cref="OsManagedSingleInstanceChannel.Deliver"/> — the same seam the shared startup
/// sequence already serves through <c>SingleInstanceGate.Serve</c>, so nothing above this line knows the difference
/// between a file that came down a named pipe and one that came from the Finder.
/// </summary>
/// <remarks>
/// Both delegate messages are covered. <c>application:openURLs:</c> is the modern one and Avalonia's own application
/// delegate implements it, raising <see cref="IActivatableLifetime.Activated"/>; the older <c>application:openFile:</c>
/// pair, which an <c>odoc</c> Apple Event still sends, is added to that delegate's class only where it is missing
/// (see <see cref="MacOpenDocuments"/>).
/// </remarks>
internal static class MacSingleInstance
{
    private const string Category = "SingleInstance";

    internal static void Attach(Application application, ISingleInstanceChannel? channel, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(application);
        ArgumentNullException.ThrowIfNull(log);
        if (channel is not OsManagedSingleInstanceChannel osManaged)
        {
            return;   // --capture runs with no channel at all
        }

        void Deliver(IReadOnlyList<string> files)
        {
            if (files.Count == 0)
            {
                return;
            }

            log.Write(AppLogLevel.Info, Category, $"The system handed this instance {files.Count} file(s) to open.");
            osManaged.Deliver(new OpenFilesRequest(files, Environment.CurrentDirectory));
        }

        AttachActivation(application, Deliver, log);
        MacOpenDocuments.InstallLegacyHandlers(Deliver, log);
    }

    private static void AttachActivation(Application application, Action<IReadOnlyList<string>> deliver, IAppLog log)
    {
        if (application.TryGetFeature(typeof(IActivatableLifetime)) is not IActivatableLifetime lifetime)
        {
            log.Write(AppLogLevel.Warning, Category,
                "This Avalonia backend has no activatable lifetime; files opened from the Finder rely on the legacy messages.");
            return;
        }

        lifetime.Activated += (_, e) =>
        {
            if (e is not FileActivatedEventArgs files)
            {
                return;
            }

            var paths = new List<string>(files.Files.Count);
            foreach (IStorageItem item in files.Files)
            {
                if (item.TryGetLocalPath() is { Length: > 0 } path)
                {
                    paths.Add(path);
                }
            }

            deliver(paths);
        };
    }
}
