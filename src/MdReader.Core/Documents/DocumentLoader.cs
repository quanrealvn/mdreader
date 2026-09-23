namespace MdReader.Core.Documents;

/// <summary>
/// Reads and decodes a document from disk (ARCHITECTURE §4.4). Never throws for file-system problems: they become
/// <see cref="DocumentLoadFailed"/>. Cancellation throws <see cref="OperationCanceledException"/>.
/// </summary>
public sealed class DocumentLoader : IDocumentLoader
{
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);

    /// <summary>Upper bound for a single read call, so a huge buffer isn't requested from the OS at once.</summary>
    private const int MaxReadChunk = 1024 * 1024;

    private static readonly DocumentLoaderOptions DefaultOptions = new();

    private readonly DocumentLoaderOptions _options;
    private readonly TimeProvider _timeProvider;

    public DocumentLoader(DocumentLoaderOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? DefaultOptions;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<DocumentLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);

        var retryDelays = _options.SharingViolationRetryDelays ?? [];
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await LoadOnceAsync(path, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex) when (IsSharingViolation(ex))
            {
                if (attempt >= retryDelays.Count)
                {
                    return new DocumentLoadFailed(path, DocumentLoadError.Locked, ex.Message);
                }

                var delay = retryDelays[attempt];
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or DriveNotFoundException)
            {
                return new DocumentLoadFailed(path, DocumentLoadError.NotFound, ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                // Opening a folder as a file is reported as "access denied" by Windows.
                return IsDirectory(path)
                    ? new DocumentLoadFailed(path, DocumentLoadError.IoError, DocumentErrorMessages.FolderDetail)
                    : new DocumentLoadFailed(path, DocumentLoadError.AccessDenied, ex.Message);
            }
            catch (IOException ex)
            {
                return IsDirectory(path)
                    ? new DocumentLoadFailed(path, DocumentLoadError.IoError, DocumentErrorMessages.FolderDetail)
                    : new DocumentLoadFailed(path, DocumentLoadError.IoError, ex.Message);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // Empty or malformed path.
                return new DocumentLoadFailed(path, DocumentLoadError.IoError, ex.Message);
            }
        }
    }

    private async Task<DocumentLoadResult> LoadOnceAsync(string path, CancellationToken cancellationToken)
    {
        // A byte[] can't hold more than Array.MaxLength bytes; one slot is kept to detect growth past the limit.
        var maxBytes = Math.Clamp(_options.MaxBytes, 0, Array.MaxLength - 1);

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1, FileOptions.SequentialScan | FileOptions.Asynchronous);

        var length = stream.Length;
        if (length > maxBytes)
        {
            return new DocumentLoadFailed(path, DocumentLoadError.TooLarge, DocumentErrorMessages.TooLargeDetail(length, maxBytes));
        }

        // Read until EOF, but never more than maxBytes + 1 bytes: the file may grow while it is being read.
        var limit = (int)maxBytes + 1;
        var buffer = new byte[(int)length + 1];
        var total = 0;
        while (true)
        {
            if (total == buffer.Length)
            {
                if (buffer.Length >= limit)
                {
                    break;
                }

                Array.Resize(ref buffer, (int)Math.Min((long)buffer.Length * 2, limit));
            }

            var chunk = Math.Min(buffer.Length - total, MaxReadChunk);
            var read = await stream.ReadAsync(buffer.AsMemory(total, chunk), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total > maxBytes)
        {
            var currentLength = Math.Max(total, SafeLength(stream));
            return new DocumentLoadFailed(path, DocumentLoadError.TooLarge, DocumentErrorMessages.TooLargeDetail(currentLength, maxBytes));
        }

        var lastWriteTimeUtc = File.GetLastWriteTimeUtc(stream.SafeFileHandle);

        var decoded = TextDecoder.Decode(buffer.AsSpan(0, total), _options.FallbackCodePage);
        if (decoded.IsBinary)
        {
            return new DocumentLoadFailed(path, DocumentLoadError.Binary, DocumentErrorMessages.BinaryDetail);
        }

        return new DocumentLoaded(path, decoded.Text, decoded.EncodingName, decoded.UsedFallbackEncoding, total, lastWriteTimeUtc)
        {
            HasBom = TextDecoder.HasByteOrderMark(buffer.AsSpan(0, total)),
        };
    }

    private static bool IsSharingViolation(IOException ex) => ex.HResult is ErrorSharingViolation or ErrorLockViolation;

    private static bool IsDirectory(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static long SafeLength(FileStream stream)
    {
        try
        {
            return stream.Length;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
