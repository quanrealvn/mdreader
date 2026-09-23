using System.Diagnostics;
using MdReader.Core.Cli;
using MdReader.Core.Diagnostics;
using MdReader.Core.SingleInstance;
using MdReader.Shell.Services;
using MdReader.Shell.Threading;

namespace MdReader.Shell.Hosting;

/// <summary>
/// The single-instance half of the startup sequence (§6, §4.11), with no UI framework in it: become the primary
/// instance or forward to the one that is already running, and then serve the files later launches forward until the
/// orderly shutdown stops it.
/// </summary>
/// <remarks>
/// The instance identity and the call that lets the running process take the foreground stay in the shells: both are
/// Windows APIs this assembly deliberately doesn't reference.
/// </remarks>
public static class SingleInstanceGate
{
    private const string Category = "Startup";
    private static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Returns the channel when this process is the primary instance; null when it forwarded
    /// (<paramref name="forwarded"/> = true) or has to run standalone because forwarding failed.
    /// </summary>
    /// <param name="allowSetForegroundWindow">
    /// <c>AllowSetForegroundWindow(pid)</c> for the primary instance's process id, so its window may come forward.
    /// </param>
    public static ISingleInstanceChannel? Acquire(CommandLineOptions options, SingleInstanceIdentity identity,
                                                  Action<int> allowSetForegroundWindow, IAppLog log, out bool forwarded)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(allowSetForegroundWindow);
        ArgumentNullException.ThrowIfNull(log);

        forwarded = false;
        var channel = new SingleInstanceChannel(identity, log);
        if (channel.TryBecomePrimary())
        {
            return channel;
        }

        ForwardResult result;
        try
        {
            // Safe to block: no SynchronizationContext exists before the UI framework is initialized (§5).
            result = channel.ForwardAsync(new OpenFilesRequest(options.Files, Environment.CurrentDirectory),
                                          allowSetForegroundWindow, ForwardTimeout)
                            .GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException
                                       or OperationCanceledException)
        {
            log.Write(AppLogLevel.Warning, Category, "Forwarding to the running instance failed", ex);
            result = ForwardResult.NoServer;
        }

        channel.Dispose();
        if (result == ForwardResult.Forwarded)
        {
            log.Write(AppLogLevel.Info, Category, $"Forwarded {options.Files.Count} file(s) to the running instance");
            forwarded = true;
            return null;
        }

        log.Write(AppLogLevel.Warning, Category, $"Couldn't forward to the running instance ({result}); running standalone");
        return null;
    }

    /// <summary>
    /// Primary instance: accept files forwarded by later launches (§4.11) until the returned stop action runs (the first
    /// step of the orderly shutdown). No forwarded file is ever lost:
    /// <list type="bullet">
    /// <item>after the stop, the server is gone (late secondaries get NoServer) and a request racing with it is rejected
    /// before its ACK (the secondary gets Rejected); either way the secondary opens its files itself;</item>
    /// <item>a request already acknowledged but not yet opened (its dispatcher callback hadn't run, or never will because
    /// the dispatcher is shutting down) is taken over by the stop action, which relaunches MdReader with those files once
    /// the pipe and mutex are released, so the new process becomes the primary instance.</item>
    /// </list>
    /// </summary>
    public static Action Serve(ISingleInstanceChannel channel, CommandLineOptions options, IUiDispatcher dispatcher,
                               IDocumentOpener opener, Action activateWindow, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(opener);
        ArgumentNullException.ThrowIfNull(activateWindow);
        ArgumentNullException.ThrowIfNull(log);

        var gate = new Lock();
        var closing = false;                              // guarded by gate
        var pending = new List<OpenFilesRequest>();       // acknowledged, not yet opened; guarded by gate

        channel.FilesReceived += (_, request) =>
        {
            // Pipe server thread, before the ACK: throwing makes the channel reject the request (the sender opens standalone).
            lock (gate)
            {
                if (closing)
                {
                    log.Write(AppLogLevel.Info, Category, $"Refusing {request.Files.Count} forwarded file(s): MdReader is closing");
                    throw new InvalidOperationException("MdReader is closing; the request is refused so the sender opens the files itself.");
                }

                pending.Add(request);
            }

            dispatcher.Post(() =>
            {
                bool relaunch;
                lock (gate)
                {
                    if (!pending.Remove(request))
                    {
                        return;   // already taken over by the stop action
                    }

                    relaunch = closing;
                }

                if (relaunch)
                {
                    RelaunchWithFiles(options, request.Files, log);
                    return;
                }

                log.Write(AppLogLevel.Info, Category, $"Received {request.Files.Count} file(s) from another instance");
                OpenFiles(opener, request.Files);
                activateWindow();
            });
        };

        channel.StartServer();
        return () =>
        {
            OpenFilesRequest[] orphaned;
            lock (gate)
            {
                closing = true;
                orphaned = [.. pending];
                pending.Clear();
            }

            channel.Dispose();   // stops the accept loop and releases the pipe name and mutex; the shell disposes again (no-op)
            foreach (var request in orphaned)
            {
                RelaunchWithFiles(options, request.Files, log);
            }
        };
    }

    /// CLI and pipe: open every file, activate the first (§4.11).
    public static void OpenFiles(IDocumentOpener opener, IReadOnlyList<string> files)
    {
        ArgumentNullException.ThrowIfNull(opener);
        ArgumentNullException.ThrowIfNull(files);
        for (var i = 0; i < files.Count; i++)
        {
            opener.Open(files[i], fragment: null, activate: i == 0);
        }
    }

    /// Opens forwarded files that arrived while this instance was shutting down in a new MdReader process. It keeps the
    /// isolation options (--profile-dir, --instance-id, --theme; never --capture/--perf-log) so tests stay isolated; it finds
    /// no running server and becomes the primary instance (or runs standalone).
    private static void RelaunchWithFiles(CommandLineOptions options, IReadOnlyList<string> files, IAppLog log)
    {
        if (files.Count == 0)
        {
            return;   // an "activate only" request: nothing to hand over
        }

        try
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("The MdReader executable path is unknown.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory };
            if (options.ProfileDirectory is { } profile)
            {
                start.ArgumentList.Add("--profile-dir");
                start.ArgumentList.Add(profile);
            }

            if (!string.Equals(options.InstanceId, CommandLineOptions.DefaultInstanceId, StringComparison.Ordinal))
            {
                start.ArgumentList.Add("--instance-id");
                start.ArgumentList.Add(options.InstanceId);
            }

            if (options.ThemeOverride is { } theme)
            {
                start.ArgumentList.Add("--theme");
                start.ArgumentList.Add(theme.ToString().ToLowerInvariant());
            }

            start.ArgumentList.Add("--");
            foreach (var file in files)
            {
                start.ArgumentList.Add(file);
            }

            using var process = Process.Start(start);
            log.Write(AppLogLevel.Info, Category,
                $"MdReader is closing: relaunched {files.Count} forwarded file(s) in a new process (pid {process?.Id})");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            log.Write(AppLogLevel.Error, Category, $"Couldn't relaunch MdReader for {files.Count} forwarded file(s)", ex);
        }
    }
}
