namespace MdReader.Core.SingleInstance;

/// <summary>
/// Identifies the one instance a user's files should be handed to. <paramref name="UserId"/> is the account the
/// process runs as (the SID on Windows, the uid on POSIX) and keeps one user's instance out of another's.
/// </summary>
/// <remarks>The two names are only meaningful to a channel that has to build the rendezvous itself
/// (<see cref="SingleInstanceMode.HostManaged"/>); where the OS manages instances they are unused.</remarks>
public sealed record SingleInstanceIdentity(string UserId, int SessionId, string InstanceId)
{
    public string MutexName => $@"Local\MdReader.{UserId}.{InstanceId}";
    public string PipeName  => $"MdReader.{UserId}.{SessionId}.{InstanceId}";
}

public sealed record OpenFilesRequest(IReadOnlyList<string> Files, string Cwd);   // Files absolute (client resolves)

public enum ForwardResult { Forwarded, NoServer, Rejected }

/// <summary>Who keeps the application to one instance and delivers the files a second launch was given.</summary>
public enum SingleInstanceMode
{
    /// <summary>MdReader does it: a named mutex decides who is primary, a named pipe carries the files.</summary>
    HostManaged,

    /// <summary>
    /// The OS does it. On macOS, LaunchServices never starts a second copy of a bundled app: it activates the running
    /// one and delivers the files through <c>application:openURLs:</c>, so there is no rendezvous to build and no
    /// second process to forward from.
    /// </summary>
    OperatingSystemManaged,
}

public interface ISingleInstanceChannel : IDisposable
{
    /// True when this process is the instance that should show the files. A host-managed channel decides with a
    /// named mutex; where the OS manages instances this is always true, because a second process never gets this far.
    bool TryBecomePrimary();
    /// Secondary only, and only meaningful when the host manages instances. Retries ConnectAsync (100 ms apart) until
    /// `timeout` elapses; the App passes 3 s.
    Task<ForwardResult> ForwardAsync(OpenFilesRequest request, Action<int> allowSetForegroundWindow,
                                     TimeSpan timeout, CancellationToken cancellationToken = default);
    /// Primary only. Starts listening for files from other launches, if this platform needs something to listen on.
    void StartServer();
    /// Raised when another launch hands this instance files to open, on a background thread.
    event EventHandler<OpenFilesRequest>? FilesReceived;
}

/// <summary>
/// Picks the single-instance implementation for the platform. The shell always writes the same startup sequence —
/// <c>TryBecomePrimary</c>, forward and exit if that fails, otherwise <c>StartServer</c> and subscribe — and a
/// platform where the OS already guarantees one instance answers it without needing a mutex or a pipe.
/// </summary>
public static class SingleInstanceChannels
{
    /// <summary>What the running OS does by itself.</summary>
    public static SingleInstanceMode DefaultMode { get; } =
        OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst()
            ? SingleInstanceMode.OperatingSystemManaged
            : SingleInstanceMode.HostManaged;

    /// <summary>The channel for <paramref name="mode"/>, or for the running OS when it is omitted.</summary>
    public static ISingleInstanceChannel Create(SingleInstanceIdentity identity, Diagnostics.IAppLog log,
                                                SingleInstanceMode? mode = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(log);

        return (mode ?? DefaultMode) switch
        {
            SingleInstanceMode.OperatingSystemManaged => new OsManagedSingleInstanceChannel(log),
            _ => new SingleInstanceChannel(identity, log),
        };
    }
}
