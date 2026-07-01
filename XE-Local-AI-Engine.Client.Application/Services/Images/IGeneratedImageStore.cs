namespace XE_Local_AI_Engine.Client.Services.Images;

/// <summary>
///     Durable encrypted-at-rest store for generated image blobs. The bytes are encrypted (AES-256-GCM, node key) and
///     written under <c>{INodeDataDirectory.Root}/generated-images/{jobId}/{imageId}.png</c>; a <c>generated_images</c>
///     metadata row (mime, dimensions, storage path, size) is persisted alongside. Mirrors the uploaded-file store: no
///     plaintext image ever lands on disk. Singleton — it opens its own DbContext scope per operation.
/// </summary>
public interface IGeneratedImageStore
{
    /// <summary>
    ///     Encrypts <paramref name="pngBytes" /> to disk under the job/image path and persists the metadata row. Returns
    ///     the stored image info.
    /// </summary>
    Task<GeneratedImageInfo> AddAsync(Guid jobId, Guid imageId, ReadOnlyMemory<byte> pngBytes, GeneratedImageMetadata metadata, CancellationToken cancellationToken);

    /// <summary>
    ///     Reads and decrypts a stored image's bytes for the retrieve endpoint, or <see langword="null" /> when the image
    ///     id is unknown or its blob is missing on disk.
    /// </summary>
    Task<GeneratedImageContent?> OpenReadAsync(Guid imageId, CancellationToken cancellationToken);
}

/// <summary>Non-secret metadata supplied when persisting a generated image.</summary>
public sealed record GeneratedImageMetadata
{
    /// <summary>Pixel width of the image.</summary>
    public required int Width { get; init; }

    /// <summary>Pixel height of the image.</summary>
    public required int Height { get; init; }

    /// <summary>MIME type of the stored image (step 1 always <c>image/png</c>).</summary>
    public string MimeType { get; init; } = "image/png";
}

/// <summary>The persisted metadata for one generated image blob (bytes live encrypted on disk).</summary>
public sealed record GeneratedImageInfo(
    Guid ImageId,
    Guid JobId,
    string MimeType,
    int Width,
    int Height,
    long SizeBytes,
    long CreatedAtUtc);

/// <summary>Decrypted image bytes plus the MIME type, returned by the retrieve path.</summary>
public sealed record GeneratedImageContent(ReadOnlyMemory<byte> Bytes, string MimeType, int Width, int Height);
