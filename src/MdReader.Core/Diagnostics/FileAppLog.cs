using System.Globalization;
using System.Text;

namespace MdReader.Core.Diagnostics;

/// <summary>
/// Writes log entries to <c>&lt;logsFolder&gt;\MdReader-yyyyMMdd.log</c> (one file per UTC day). Thread-safe (a single
/// lock serializes writes) and never throws: any I/O failure is swallowed so logging can never crash the app.
/// On construction, deletes log files older than 7 days.
/// </summary>
public sealed class FileAppLog : IAppLog, IDisposable
{
    private const string FilePrefix = "MdReader-";
    private const string FileSuffix = ".log";
    private const string DateFormat = "yyyyMMdd";
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _logsFolder;
    private readonly AppLogLevel _minimumLevel;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();

    private StreamWriter? _writer;
    private DateOnly _writerDate;
    private bool _disposed;

    public FileAppLog(string logsFolder, AppLogLevel minimumLevel, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logsFolder);

        _logsFolder = logsFolder;
        _minimumLevel = minimumLevel;
        _timeProvider = timeProvider ?? TimeProvider.System;

        DeleteOldLogs();
    }

    public void Write(AppLogLevel level, string category, string message, Exception? exception = null)
    {
        if (level < _minimumLevel)
        {
            return;
        }

        try
        {
            var now = _timeProvider.GetUtcNow();

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                var writer = GetWriter(DateOnly.FromDateTime(now.UtcDateTime));
                if (writer is null)
                {
                    return;
                }

                // Another FileAppLog instance (a different process, or the primary + a standalone fallback sharing
                // the same logs folder) may have appended to this file through its own handle since we last wrote.
                // Our FileStream only seeks to end once, at open time, so without re-seeking here each write would
                // land at our own stale cached position instead of the file's true current end, clobbering or
                // interleaving badly with the other writer's bytes. Re-querying the actual end before every entry
                // keeps concurrent writers append-only.
                writer.BaseStream.Seek(0, SeekOrigin.End);

                writer.Write(now.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture));
                writer.Write(" [");
                writer.Write(level.ToString());
                writer.Write("] ");
                writer.Write(category);
                writer.Write(": ");
                writer.WriteLine(message);

                if (exception is not null)
                {
                    writer.WriteLine(exception.ToString());
                }

                writer.Flush();
            }
        }
        catch
        {
            // Logging must never throw.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                _writer?.Dispose();
            }
            catch
            {
                // Best effort.
            }

            _writer = null;
        }
    }

    // Caller holds _gate.
    private StreamWriter? GetWriter(DateOnly date)
    {
        if (_writer is not null && _writerDate == date)
        {
            return _writer;
        }

        try
        {
            _writer?.Dispose();
        }
        catch
        {
            // Best effort.
        }

        _writer = null;

        try
        {
            Directory.CreateDirectory(_logsFolder);
            var path = Path.Combine(_logsFolder, FilePrefix + date.ToString(DateFormat, CultureInfo.InvariantCulture) + FileSuffix);
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(stream, Utf8NoBom) { AutoFlush = false };
            _writerDate = date;
            return _writer;
        }
        catch
        {
            _writer = null;
            return null;
        }
    }

    private void DeleteOldLogs()
    {
        try
        {
            if (!Directory.Exists(_logsFolder))
            {
                return;
            }

            var cutoff = DateOnly.FromDateTime(_timeProvider.GetUtcNow().UtcDateTime).AddDays(-7);

            foreach (var file in Directory.EnumerateFiles(_logsFolder, FilePrefix + "*" + FileSuffix))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (!name.StartsWith(FilePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var datePart = name[FilePrefix.Length..];
                if (!DateOnly.TryParseExact(datePart, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fileDate))
                {
                    continue;
                }

                if (fileDate < cutoff)
                {
                    try
                    {
                        File.Delete(file);
                    }
                    catch
                    {
                        // Best effort.
                    }
                }
            }
        }
        catch
        {
            // Never throw from the constructor either.
        }
    }
}
