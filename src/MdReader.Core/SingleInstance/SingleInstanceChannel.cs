using System.Diagnostics;
using System.IO.Pipes;
using MdReader.Core.Diagnostics;

namespace MdReader.Core.SingleInstance;

/// <summary>
/// Single-instance detection (named mutex) and path forwarding (named pipe, protocol v1 in <see cref="PipeFraming"/>).
/// See ARCHITECTURE §4.5.
/// </summary>
/// <remarks>
/// <para><b>Mutex and threads.</b> <see cref="TryBecomePrimary"/> creates the mutex with <c>initiallyOwned: true</c>,
/// which makes the calling thread its owner. The App MUST call it from the main thread, which lives for the whole
/// process. Ownership itself is never used: other processes only check whether the name already exists
/// (<c>createdNew</c>), and nobody waits on the mutex or calls <c>ReleaseMutex</c>. So <see cref="Dispose"/> may run on
/// any thread. It only closes the handle, and closing a mutex handle on a thread that doesn't own it doesn't throw.</para>
/// <para><b>Server.</b> A sequential accept loop runs on a background task. <see cref="FilesReceived"/> is raised on
/// that thread, before the ack is sent. Clients that misbehave (garbage, bad lengths, partial frames, disconnects,
/// stalls) are logged and dropped, and the loop moves on to the next connection. Every connection is bounded by
/// <see cref="ConnectionTimeout"/>.</para>
/// <para><b>Client.</b> <see cref="ForwardAsync"/> retries the connect every 100 ms until <c>timeout</c> has elapsed.
/// Once connected, the exchange is bounded by <see cref="ConnectionTimeout"/> rather than by what is left of
/// <c>timeout</c>. At that point the server is known to be alive, and giving up halfway through risks both the primary
/// and the standalone fallback opening the same files.</para>
/// </remarks>
public sealed class SingleInstanceChannel : ISingleInstanceChannel
{
    private const string LogCategory = "SingleInstance";

    /// Per-connection budget (server: accept → close; client: connect → ack).
    internal static readonly TimeSpan DefaultConnectionTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan ConnectRetryInterval = TimeSpan.FromMilliseconds(100);

    // The 5-argument NamedPipeServerStream constructor uses 0-byte buffers, so every write waits until the peer has
    // read it (observed on Windows 11 / .NET 10). A client that writes before reading the PID would then deadlock with
    // the server's PID write until the connection timeout. With small buffers the server's writes (PID, ack) never
    // wait on the client, and garbage is rejected right away.
    private const int PipeBufferSize = 4096;

    private static readonly TimeSpan AckDeliveryTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DisposeWaitTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxAcceptBackoff = TimeSpan.FromSeconds(2);

    private readonly SingleInstanceIdentity _identity;
    private readonly IAppLog _log;
    private readonly Lock _gate = new();

    // Guarded by _gate.
    private Mutex? _mutex;
    private CancellationTokenSource? _serverCts;
    private Task? _serverTask;
    private NamedPipeServerStream? _currentServer;
    private bool _disposed;

    // Managed thread id that is currently raising FilesReceived (0 = none); lets Dispose avoid waiting on itself.
    private int _raisingThreadId;

    public SingleInstanceChannel(SingleInstanceIdentity identity, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(log);
        _identity = identity;
        _log = log;
    }

    public event EventHandler<OpenFilesRequest>? FilesReceived;

    /// Per-connection timeout: server side from accept to close, client side from connect to ack. Tests shorten it.
    internal TimeSpan ConnectionTimeout { get; init; } = DefaultConnectionTimeout;

    /// new Mutex(initiallyOwned: true, MutexName, out createdNew). createdNew → primary (true).
    /// UnauthorizedAccessException (e.g. an elevated primary) → false. Never calls ReleaseMutex; Dispose closes the handle.
    /// Call from the App's main thread (see remarks). Calling it again after it returned true returns true.
    public bool TryBecomePrimary()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_mutex is not null)
            {
                return true;
            }

            Mutex mutex;
            bool createdNew;
            try
            {
                mutex = new Mutex(initiallyOwned: true, _identity.MutexName, out createdNew);
            }
            catch (UnauthorizedAccessException ex)
            {
                // The mutex exists but this process can't open it (e.g. it belongs to an elevated primary).
                _log.Write(AppLogLevel.Info, LogCategory,
                    $"Mutex '{_identity.MutexName}' exists but can't be opened; another instance owns it.", ex);
                return false;
            }
            catch (WaitHandleCannotBeOpenedException ex)
            {
                // Another kind of kernel object already uses the name. It isn't ours, so don't claim to be primary.
                _log.Write(AppLogLevel.Warning, LogCategory, $"Mutex '{_identity.MutexName}' can't be created.", ex);
                return false;
            }

            if (!createdNew)
            {
                mutex.Dispose();   // opened someone else's mutex: not owned by us, nothing to release
                return false;
            }

            _mutex = mutex;
            return true;
        }
    }

    /// Secondary only. Retries ConnectAsync (100 ms apart) until `timeout` elapses; the App passes 3 s.
    /// Forwarded on ack 0x01, Rejected on any other ack (or, without connecting, a request that can't be sent faithfully:
    /// invalid, > 64 KiB, or a path/cwd that isn't well-formed UTF-16),
    /// NoServer when nothing accepted the connection in time or the pipe failed. Cancellation → OperationCanceledException.
    public async Task<ForwardResult> ForwardAsync(OpenFilesRequest request, Action<int> allowSetForegroundWindow,
                                                  TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(allowSetForegroundWindow);
        ArgumentOutOfRangeException.ThrowIfLessThan(timeout, TimeSpan.Zero);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (!PipeFraming.TryCreateRequestFrame(request, out var frame, out var frameError))
        {
            _log.Write(AppLogLevel.Warning, LogCategory, $"Not forwarding: {frameError}");
            return ForwardResult.Rejected;
        }

        var stopwatch = Stopwatch.StartNew();
        var attempts = 0;
        Exception? lastError = null;
        while (true)
        {
            attempts++;
            using (var client = new NamedPipeClientStream(".", _identity.PipeName, PipeDirection.InOut,
                       PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                var connected = false;
                try
                {
                    // Timeout 0 = a single attempt: no listening instance → TimeoutException right away,
                    // all instances busy → waits at most the server's default wait (50 ms).
                    await client.ConnectAsync(0, cancellationToken).ConfigureAwait(false);
                    connected = true;
                }
                catch (TimeoutException ex)
                {
                    lastError = ex;
                }
                catch (IOException ex)
                {
                    lastError = ex;
                }
                catch (UnauthorizedAccessException ex)
                {
                    // The pipe exists but this process may not use it (another owner or a higher integrity level,
                    // e.g. an elevated primary). That doesn't change by retrying.
                    _log.Write(AppLogLevel.Warning, LogCategory, "The primary instance's pipe can't be used by this process.", ex);
                    return ForwardResult.NoServer;
                }

                if (connected)
                {
                    return await ExchangeAsync(client, frame, allowSetForegroundWindow, cancellationToken).ConfigureAwait(false);
                }
            }

            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                _log.Write(AppLogLevel.Warning, LogCategory,
                    $"No primary instance answered on pipe '{_identity.PipeName}' within {timeout.TotalMilliseconds:0} ms ({attempts} attempts).",
                    lastError);
                return ForwardResult.NoServer;
            }

            await Task.Delay(remaining < ConnectRetryInterval ? remaining : ConnectRetryInterval, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// Primary only (requires TryBecomePrimary() == true). Background accept loop; FilesReceived is raised on that
    /// background thread. Calling it again while the server runs does nothing.
    public void StartServer()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_mutex is null)
            {
                throw new InvalidOperationException("StartServer requires TryBecomePrimary() to have returned true.");
            }

            if (_serverTask is not null)
            {
                return;
            }

            _serverCts = new CancellationTokenSource();
            var stoppingToken = _serverCts.Token;
            _serverTask = Task.Run(() => AcceptLoopAsync(stoppingToken), CancellationToken.None);
        }

        _log.Write(AppLogLevel.Info, LogCategory, $"Listening on pipe '{_identity.PipeName}'.");
    }

    /// Stops the server (closes the listening pipe and waits briefly for the loop to end) and closes the mutex handle.
    /// Safe to call more than once and from any thread. Never throws.
    public void Dispose()
    {
        Mutex? mutex;
        CancellationTokenSource? serverCts;
        Task? serverTask;
        NamedPipeServerStream? currentServer;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            (mutex, serverCts, serverTask, currentServer) = (_mutex, _serverCts, _serverTask, _currentServer);
            (_mutex, _serverCts, _serverTask, _currentServer) = (null, null, null, null);
        }

        if (serverCts is not null && serverTask is not null)
        {
            try
            {
                serverCts.Cancel();
                currentServer?.Dispose();   // releases the pipe name right away

                // Don't wait for ourselves when a FilesReceived handler disposes the channel.
                if (Volatile.Read(ref _raisingThreadId) != Environment.CurrentManagedThreadId
                    && !serverTask.Wait(DisposeWaitTimeout))
                {
                    _log.Write(AppLogLevel.Warning, LogCategory, "The pipe server didn't stop within the dispose timeout.");
                }
            }
            catch (Exception ex)
            {
                _log.Write(AppLogLevel.Warning, LogCategory, "Error while stopping the pipe server.", ex);
            }

            if (serverTask.IsCompleted)
            {
                serverCts.Dispose();   // otherwise the loop still observes the token; leave the CTS to the GC
            }
        }

        mutex?.Dispose();
    }

    private async Task<ForwardResult> ExchangeAsync(NamedPipeClientStream client, byte[] frame,
        Action<int> allowSetForegroundWindow, CancellationToken cancellationToken)
    {
        using var exchangeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        exchangeCts.CancelAfter(ConnectionTimeout);
        var serverProcessId = 0;
        try
        {
            var result = await PipeFraming.RunClientExchangeAsync(client, frame, pid =>
            {
                serverProcessId = pid;
                try
                {
                    allowSetForegroundWindow(pid);
                }
                catch (Exception ex)
                {
                    // Losing the foreground hand-off is cosmetic; forwarding the files matters more.
                    _log.Write(AppLogLevel.Warning, LogCategory, "allowSetForegroundWindow failed.", ex);
                }
            }, exchangeCts.Token).ConfigureAwait(false);

            _log.Write(result == ForwardResult.Forwarded ? AppLogLevel.Info : AppLogLevel.Warning, LogCategory,
                $"Forward to the primary instance (pid {serverProcessId}): {result}.");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Exchange timeout, server disconnect, protocol violation: the standalone fallback is always safe.
            _log.Write(AppLogLevel.Warning, LogCategory, "Forwarding to the primary instance failed.", ex);
            return ForwardResult.NoServer;
        }
    }

    private async Task AcceptLoopAsync(CancellationToken stoppingToken)
    {
        var consecutiveFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(_identity.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, PipeBufferSize, PipeBufferSize);
                if (!TrackServer(server))
                {
                    break;   // disposed while the instance was being created
                }

                await server.WaitForConnectionAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ReleaseServer(server);
                if (stoppingToken.IsCancellationRequested)
                {
                    break;   // OperationCanceledException, or ObjectDisposedException because Dispose closed the pipe
                }

                // Creating or listening failed (not a client's fault). Back off so a persistent failure isn't a hot loop.
                consecutiveFailures++;
                _log.Write(AppLogLevel.Warning, LogCategory, $"Pipe server error (attempt {consecutiveFailures}).", ex);
                var backoff = TimeSpan.FromMilliseconds(Math.Min(100.0 * consecutiveFailures, MaxAcceptBackoff.TotalMilliseconds));
                if (!await DelayAsync(backoff, stoppingToken).ConfigureAwait(false))
                {
                    break;
                }

                continue;
            }

            consecutiveFailures = 0;
            try
            {
                await HandleConnectionAsync(server, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Write(AppLogLevel.Warning, LogCategory, "Unexpected error while handling a pipe connection.", ex);
            }
            finally
            {
                ReleaseServer(server);
            }
        }

        _log.Write(AppLogLevel.Debug, LogCategory, "Pipe server stopped.");
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken stoppingToken)
    {
        using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        connectionCts.CancelAfter(ConnectionTimeout);
        try
        {
            var outcome = await PipeFraming.RunServerExchangeAsync(server, Environment.ProcessId, RaiseFilesReceived,
                connectionCts.Token).ConfigureAwait(false);
            if (outcome.Accepted)
            {
                _log.Write(AppLogLevel.Info, LogCategory, $"Accepted {outcome.Request!.Files.Count} file(s) from another instance.");
            }
            else
            {
                _log.Write(AppLogLevel.Warning, LogCategory, $"Rejected a request from another instance: {outcome.RejectReason}");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (OperationCanceledException)
        {
            _log.Write(AppLogLevel.Warning, LogCategory,
                $"A pipe client didn't complete the exchange within {ConnectionTimeout.TotalMilliseconds:0} ms; dropped.");
            return;
        }
        catch (Exception ex)
        {
            _log.Write(AppLogLevel.Warning, LogCategory, "A pipe client disconnected or sent an invalid frame; dropped.", ex);
            return;
        }

        // Keep our end open until the client has read the ack and closed its end, so closing can't drop the ack.
        try
        {
            using var ackCts = CancellationTokenSource.CreateLinkedTokenSource(connectionCts.Token);
            ackCts.CancelAfter(AckDeliveryTimeout);
            await PipeFraming.WaitForPeerCloseAsync(server, ackCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The ack was written; nothing more to do for this client.
        }
    }

    private bool RaiseFilesReceived(OpenFilesRequest request)
    {
        var handler = FilesReceived;
        if (handler is null)
        {
            // Nobody would open the files: reject so the secondary opens them itself instead of losing them.
            _log.Write(AppLogLevel.Warning, LogCategory, "Files received but nothing is subscribed to FilesReceived.");
            return false;
        }

        Volatile.Write(ref _raisingThreadId, Environment.CurrentManagedThreadId);
        try
        {
            handler(this, request);
            return true;
        }
        catch (Exception ex)
        {
            // The primary couldn't take the files: reject, so the secondary opens them standalone instead.
            _log.Write(AppLogLevel.Error, LogCategory, "A FilesReceived handler threw; rejecting the request.", ex);
            return false;
        }
        finally
        {
            Volatile.Write(ref _raisingThreadId, 0);
        }
    }

    private bool TrackServer(NamedPipeServerStream server)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                server.Dispose();
                return false;
            }

            _currentServer = server;
            return true;
        }
    }

    private void ReleaseServer(NamedPipeServerStream? server)
    {
        if (server is null)
        {
            return;
        }

        lock (_gate)
        {
            if (ReferenceEquals(_currentServer, server))
            {
                _currentServer = null;
            }
        }

        server.Dispose();
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
