namespace MdReader.Core.Documents;

public interface IDocumentLoader
{
    Task<DocumentLoadResult> LoadAsync(string path, CancellationToken cancellationToken = default);
}

public abstract record DocumentLoadResult(string Path);

public sealed record DocumentLoaded(string Path, string Text, string EncodingName, bool UsedFallbackEncoding,
                                    long ByteLength, DateTime LastWriteTimeUtc) : DocumentLoadResult(Path)
{
    /// The file started with a byte order mark. Saving re-emits exactly that (<see cref="DocumentTextEncoder"/>):
    /// UTF-16/32 are also detected without a BOM, so the encoding name alone doesn't say.
    public bool HasBom { get; init; }
}

public sealed record DocumentLoadFailed(string Path, DocumentLoadError Error, string Detail) : DocumentLoadResult(Path);

public enum DocumentLoadError { NotFound, AccessDenied, Locked, Binary, TooLarge, IoError }

public sealed record DocumentLoaderOptions
{
    public const long DefaultMaxBytes = 20L * 1024 * 1024;
    public long MaxBytes { get; init; } = DefaultMaxBytes;
    /// Code page used when the bytes are not valid UTF-8. The App sets CultureInfo.CurrentCulture.TextInfo.ANSICodePage
    /// (e.g. 1258 on Vietnamese Windows); unsupported/0 → 1252.
    public int FallbackCodePage { get; init; } = 1252;
    public IReadOnlyList<TimeSpan> SharingViolationRetryDelays { get; init; } =
        [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(400)];
}
