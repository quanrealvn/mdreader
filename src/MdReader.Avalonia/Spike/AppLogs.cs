using MdReader.Core.Diagnostics;

namespace MdReader.Avalonia.Spike;

/// <summary>Writes every entry to each of the given logs. Never throws.</summary>
internal sealed class CompositeAppLog(params IAppLog[] logs) : IAppLog
{
    private readonly IAppLog[] _logs = logs;

    public void Write(AppLogLevel level, string category, string message, Exception? exception = null)
    {
        foreach (IAppLog log in _logs)
        {
            try
            {
                log.Write(level, category, message, exception);
            }
            catch (Exception)
            {
                // Logging must never crash the app.
            }
        }
    }
}

/// <summary>
/// Mirrors the log to stderr so a headless <c>--capture</c> run says what happened without anyone opening the log file.
/// The process is a WinExe, so this only reaches a caller that redirected the handle.
/// </summary>
internal sealed class StandardErrorAppLog(AppLogLevel minimumLevel) : IAppLog
{
    private readonly AppLogLevel _minimumLevel = minimumLevel;
    private readonly Lock _gate = new();

    public void Write(AppLogLevel level, string category, string message, Exception? exception = null)
    {
        if (level < _minimumLevel)
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                Console.Error.WriteLine(exception is null
                    ? $"[{level}] {category}: {message}"
                    : $"[{level}] {category}: {message} :: {exception}");
                Console.Error.Flush();
            }
            catch (IOException)
            {
                // No console attached.
            }
        }
    }
}
