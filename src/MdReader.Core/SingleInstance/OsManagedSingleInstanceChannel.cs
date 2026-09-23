using MdReader.Core.Diagnostics;

namespace MdReader.Core.SingleInstance;

/// <summary>
/// The channel for platforms that keep the application to one instance themselves
/// (<see cref="SingleInstanceMode.OperatingSystemManaged"/>). It owns no mutex and no pipe: this process is always the
/// primary, there is nothing to listen on, and the files arrive from the platform shell rather than from a second
/// process, through <see cref="Deliver"/>.
/// </summary>
/// <remarks>
/// <para><b>Not implemented yet (AVALONIA-PLAN.md phase 2).</b> Nothing calls <see cref="Deliver"/> so far. The macOS
/// shell has to call it from its application delegate: <c>application:openURLs:</c> (and
/// <c>application:openFile:</c> for the documents a launch is started with) hands over the file URLs, which the shell
/// turns into an <see cref="OpenFilesRequest"/> with absolute paths and the process's working directory. The native
/// side of that belongs to the Avalonia shell, not to Core; this class is the seam it plugs into, so the startup
/// sequence in the shared shell does not have to know which platform it is on.</para>
/// <para>Files are delivered on whatever thread calls <see cref="Deliver"/>, matching the host-managed channel's
/// contract that <see cref="FilesReceived"/> is not raised on the UI thread; subscribers already marshal.</para>
/// </remarks>
public sealed class OsManagedSingleInstanceChannel : ISingleInstanceChannel
{
    private const string LogCategory = "SingleInstance";

    private readonly IAppLog _log;
    private bool _disposed;

    public OsManagedSingleInstanceChannel(IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    public event EventHandler<OpenFilesRequest>? FilesReceived;

    /// <summary>Always true: the OS never lets a second instance reach this point.</summary>
    public bool TryBecomePrimary()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return true;
    }

    /// <summary>
    /// Never reached in a correct startup sequence, because <see cref="TryBecomePrimary"/> always succeeds. Returning
    /// <see cref="ForwardResult.NoServer"/> keeps a caller that gets here running standalone rather than losing files.
    /// </summary>
    public Task<ForwardResult> ForwardAsync(OpenFilesRequest request, Action<int> allowSetForegroundWindow,
                                            TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(allowSetForegroundWindow);
        cancellationToken.ThrowIfCancellationRequested();

        _log.Write(AppLogLevel.Warning, LogCategory,
            "Nothing to forward to: this platform manages single instance itself, so this process is the primary.");
        return Task.FromResult(ForwardResult.NoServer);
    }

    /// <summary>Nothing to listen on: the platform shell delivers files through <see cref="Deliver"/>.</summary>
    public void StartServer()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _log.Write(AppLogLevel.Debug, LogCategory, "Single instance is managed by the operating system; no server started.");
    }

    /// <summary>
    /// Hands files from the platform shell to the application. Returns false if nobody is listening, so the caller can
    /// hold them until the window exists. Never throws: a handler that fails is logged and reported as not delivered,
    /// the same way the pipe server rejects a request its handler threw on.
    /// </summary>
    public bool Deliver(OpenFilesRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var handler = FilesReceived;
        if (handler is null)
        {
            _log.Write(AppLogLevel.Warning, LogCategory, "Files received but nothing is subscribed to FilesReceived.");
            return false;
        }

        try
        {
            handler(this, request);
            _log.Write(AppLogLevel.Info, LogCategory, $"Accepted {request.Files.Count} file(s) from the operating system.");
            return true;
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Error, LogCategory, "A FilesReceived handler threw; the files were not opened.", ex);
            return false;
        }
    }

    public void Dispose() => _disposed = true;
}
