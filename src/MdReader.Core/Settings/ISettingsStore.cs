namespace MdReader.Core.Settings;

public interface ISettingsStore
{
    string FilePath { get; }
    /// Never throws. Missing → defaults. Corrupt/unreadable → copy to "settings.json.bak" (overwrite), log, defaults.
    AppSettings Load();
    /// Atomic: write "settings.json.tmp", then File.Move(tmp, FilePath, overwrite: true). Throws IOException (caller logs).
    void Save(AppSettings settings);
}
