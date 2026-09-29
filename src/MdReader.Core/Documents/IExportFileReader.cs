using System.IO;

namespace MdReader.Core.Documents;

/// <summary>
/// Reads the files an HTML export has to carry with it: the reader's own stylesheet and icons from the application
/// folder, and whatever local images the document points at.
/// </summary>
/// <remarks>
/// A seam rather than direct <see cref="File"/> calls, for two reasons. The exporter can then be tested without a
/// disk — the interesting cases are "this image is 40 MB" and "this image is locked", which are miserable to stage
/// for real — and every read in the export has exactly one ceiling and exactly one failure answer: <c>null</c>. An
/// export must never fail, or hang, because one picture is unreadable.
/// </remarks>
public interface IExportFileReader
{
    /// <summary>
    /// The file's bytes, or null when it is missing, unreadable, not a file, or longer than
    /// <paramref name="maxBytes"/>. Never throws.
    /// </summary>
    byte[]? TryRead(string fullPath, long maxBytes);
}

/// <summary>The real file system. Every failure is a <c>null</c>; nothing is logged from here (the caller counts).</summary>
public sealed class PhysicalExportFileReader : IExportFileReader
{
    public static PhysicalExportFileReader Instance { get; } = new();

    private PhysicalExportFileReader()
    {
    }

    public byte[]? TryRead(string fullPath, long maxBytes)
    {
        if (string.IsNullOrEmpty(fullPath) || maxBytes <= 0)
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
            if (stream.Length > maxBytes)
            {
                return null;
            }

            // Read into a buffer sized from Length but stop at maxBytes anyway: on a file that is growing (a
            // screenshot being written while the export runs) Length is a hint, not a promise, and a ceiling that
            // only holds for well-behaved files is not a ceiling.
            using var buffer = new MemoryStream(checked((int)stream.Length));
            var chunk = new byte[81920];
            long total = 0;
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    return null;
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException
                                       or ArgumentException or System.Security.SecurityException
                                       or OverflowException or OutOfMemoryException)
        {
            return null;
        }
    }
}
