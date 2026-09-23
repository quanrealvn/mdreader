using System.IO;

namespace MdReader.Core.Documents;

/// <summary>
/// Saves a document without ever truncating the version that is already on disk (ARCHITECTURE §4.4). The bytes go to a
/// temporary file next to the document and only take its place once they are completely written, so a crash, a full disk
/// or a network share that disappears mid-write can cost the new version but never the old one. It also saves documents
/// a plain <see cref="FileMode.Create"/> refuses to open (hidden ones), and keeps the original's attributes.
/// </summary>
public static class AtomicFileWrite
{
    /// <summary>Attributes worth carrying over from the replaced document (the rest are the file system's business).</summary>
    private const FileAttributes Carried =
        FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly | FileAttributes.NotContentIndexed;

    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="path"/>, replacing the file there if it exists. Throws the same
    /// kinds of exception a direct write does: <see cref="IOException"/> (locked, disk full, share gone),
    /// <see cref="UnauthorizedAccessException"/> (read-only file, denied ACL), <see cref="NotSupportedException"/>,
    /// <see cref="ArgumentException"/>.
    /// </summary>
    public static void Write(string path, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(bytes);

        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(directory))
        {
            throw new IOException($"'{full}' has no folder to write into.");
        }

        FileAttributes? existing = TryGetAttributes(full);
        if (existing is { } attributes && attributes.HasFlag(FileAttributes.ReadOnly))
        {
            // Replacing a read-only file would succeed where writing it fails: keep refusing it, with the usual message.
            throw new UnauthorizedAccessException($"Access to the path '{full}' is denied.");
        }

        // Same folder: a rename inside one volume is what makes the swap atomic (and keeps the data off other volumes).
        var temporary = Path.Combine(directory, $"{Path.GetFileName(full)}.{Guid.NewGuid():N}.mdrtmp");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            if (existing is null)
            {
                File.Move(temporary, full);   // nothing to replace
                return;
            }

            Replace(temporary, full);
            RestoreAttributes(full, existing.Value);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void Replace(string temporary, string target)
    {
        Exception? failure = TryReplace(temporary, target);
        if (failure is null)
        {
            return;
        }

        if (failure is UnauthorizedAccessException)
        {
            throw failure;   // an ACL or a read-only file: renaming over it wouldn't work either
        }

        // ReplaceFile isn't supported everywhere (some network shares, FAT volumes): a rename over the target is the
        // next best thing — the old bytes still stay intact until the new ones are complete.
        if (TryMove(temporary, target) is not null)
        {
            throw failure;   // the ReplaceFile failure says more (sharing violation, disk full, …) than the rename's
        }
    }

    private static Exception? TryReplace(string temporary, string target)
    {
        try
        {
            File.Replace(temporary, target, destinationBackupFileName: null, ignoreMetadataErrors: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or PlatformNotSupportedException)
        {
            return ex;
        }
    }

    private static Exception? TryMove(string temporary, string target)
    {
        try
        {
            File.Move(temporary, target, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ex;
        }
    }

    private static FileAttributes? TryGetAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Gives the saved document the flags the replaced one had (hidden above all) and none the temp file added.</summary>
    private static void RestoreAttributes(string path, FileAttributes original)
    {
        try
        {
            FileAttributes current = File.GetAttributes(path);
            FileAttributes wanted = (current & ~Carried) | (original & Carried);
            if (wanted != current)
            {
                File.SetAttributes(path, wanted);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The document is saved; its attributes are cosmetic next to that.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            // A leftover temp file is the least of the caller's problems.
        }
    }
}
