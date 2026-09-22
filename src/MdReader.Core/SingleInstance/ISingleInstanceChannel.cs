namespace MdReader.Core.SingleInstance;

public sealed record SingleInstanceIdentity(string UserSid, int SessionId, string InstanceId)
{
    public string MutexName => $@"Local\MdReader.{UserSid}.{InstanceId}";
    public string PipeName  => $"MdReader.{UserSid}.{SessionId}.{InstanceId}";
}

public sealed record OpenFilesRequest(IReadOnlyList<string> Files, string Cwd);   // Files absolute (client resolves)

public enum ForwardResult { Forwarded, NoServer, Rejected }

public interface ISingleInstanceChannel : IDisposable
{
    /// new Mutex(initiallyOwned: true, MutexName, out createdNew). createdNew → primary (true).
    /// UnauthorizedAccessException (e.g. an elevated primary) → false. Never calls ReleaseMutex; Dispose closes the handle.
    bool TryBecomePrimary();
    /// Secondary only. Retries ConnectAsync (100 ms apart) until `timeout` elapses; the App passes 3 s.
    Task<ForwardResult> ForwardAsync(OpenFilesRequest request, Action<int> allowSetForegroundWindow,
                                     TimeSpan timeout, CancellationToken cancellationToken = default);
    /// Primary only. Background accept loop; FilesReceived is raised on that background thread.
    void StartServer();
    event EventHandler<OpenFilesRequest>? FilesReceived;
}
