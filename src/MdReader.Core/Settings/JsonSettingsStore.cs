using System.Text.Json;
using MdReader.Core.Diagnostics;

namespace MdReader.Core.Settings;

// Owned by WP4 (ARCHITECTURE §2.2, §4.6).
public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string _filePath;
    private readonly IAppLog _log;

    public JsonSettingsStore(string filePath, IAppLog log)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        ArgumentNullException.ThrowIfNull(log);

        _filePath = filePath;
        _log = log;
    }

    public string FilePath => _filePath;

    public AppSettings Load()
    {
        string json;
        try
        {
            if (!File.Exists(_filePath))
            {
                return AppSettingsNormalizer.Normalize(new AppSettings());
            }

            json = File.ReadAllText(_filePath);
        }
        catch (Exception ex)
        {
            // The file exists but couldn't be read right now (locked by another process, permissions, a transient
            // share violation, ...). That's not corruption, so it isn't backed up: it may well be readable next time.
            _log.Write(AppLogLevel.Warning, "Settings", $"Could not read settings from '{_filePath}'; using defaults for this session.", ex);
            return AppSettingsNormalizer.Normalize(new AppSettings());
        }

        try
        {
            var dto = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettingsDto)
                ?? throw new JsonException("Deserialized settings document was null.");

            return AppSettingsNormalizer.Normalize(ToAppSettings(dto));
        }
        catch (Exception ex)
        {
            // The content itself is malformed: back it up so it isn't silently lost, then fall back to defaults.
            TryBackupCorruptFile();
            _log.Write(AppLogLevel.Warning, "Settings", $"Settings file '{_filePath}' is corrupt; backed up to '.bak' and using defaults.", ex);
            return AppSettingsNormalizer.Normalize(new AppSettings());
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tmpPath = _filePath + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, SettingsJsonContext.Default.AppSettings);

        using (var stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tmpPath, _filePath, overwrite: true);
    }

    // A property absent from the JSON (null on the DTO) takes new AppSettings()'s initializer default rather than
    // the DTO field's CLR default -- see AppSettingsDto for why that distinction matters.
    private static AppSettings ToAppSettings(AppSettingsDto dto)
    {
        var defaults = new AppSettings();

        return new AppSettings
        {
            SchemaVersion = dto.SchemaVersion ?? defaults.SchemaVersion,
            Theme = dto.Theme ?? defaults.Theme,
            Zoom = dto.Zoom ?? defaults.Zoom,
            TocVisible = dto.TocVisible ?? defaults.TocVisible,
            TocWidth = dto.TocWidth ?? defaults.TocWidth,
            SplitView = dto.SplitView ?? defaults.SplitView,
            SplitRatio = dto.SplitRatio ?? defaults.SplitRatio,
            Window = dto.Window ?? defaults.Window,
            RecentFiles = dto.RecentFiles ?? defaults.RecentFiles,
            Session = dto.Session ?? defaults.Session,
        };
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                File.Copy(_filePath, _filePath + ".bak", overwrite: true);
            }
        }
        catch
        {
            // Best effort: never throw from Load.
        }
    }
}
