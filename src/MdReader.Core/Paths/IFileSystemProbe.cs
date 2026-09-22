namespace MdReader.Core.Paths;

public interface IFileSystemProbe
{
    bool FileExists(string path);
    bool DirectoryExists(string path);
    DateTime? GetLastWriteTimeUtc(string path);     // null if missing or inaccessible
}

public sealed class PhysicalFileSystemProbe : IFileSystemProbe
{
    public static PhysicalFileSystemProbe Instance { get; } = new();

    private PhysicalFileSystemProbe()
    {
    }

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public DateTime? GetLastWriteTimeUtc(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.LastWriteTimeUtc : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
                                       or System.Security.SecurityException)
        {
            return null;
        }
    }
}
