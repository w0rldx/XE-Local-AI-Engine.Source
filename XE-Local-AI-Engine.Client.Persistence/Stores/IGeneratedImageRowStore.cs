namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for the <c>generated_images</c> metadata rows.
/// </summary>
/// <remarks>
///     Only the rows. The image bytes are encrypted and written to disk by the application-layer blob store that calls
///     this one, so nothing here carries a payload; the row records where those bytes landed. No column of it is
///     encrypted, which is why it is written over the raw-SQL path rather than through the save-changes interceptor.
///     Consumed through a fresh DI scope per operation.
/// </remarks>
public interface IGeneratedImageRowStore
{
    /// <summary>Inserts one metadata row for an image whose bytes are already on disk at <paramref name="storagePath" />.</summary>
    Task InsertAsync(GeneratedImageRow row, string storagePath, CancellationToken cancellationToken);

    /// <summary>
    ///     Reads where one image's bytes live and how to serve them, or <see langword="null" /> when the id is unknown.
    /// </summary>
    Task<GeneratedImageLocation?> FindAsync(Guid imageId, CancellationToken cancellationToken);
}

/// <summary>One <c>generated_images</c> row as the blob store hands it over.</summary>
public sealed class GeneratedImageRow
{
    public required Guid ImageId { get; init; }

    public required Guid JobId { get; init; }

    public required string MimeType { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required long SizeBytes { get; init; }

    public required long CreatedAtUtc { get; init; }
}

/// <summary>Where one stored image's bytes are, and what the retrieve path needs to describe them.</summary>
public sealed class GeneratedImageLocation
{
    /// <summary>The owning job, which is half of the blob's associated data and so decides whether it decrypts.</summary>
    public required Guid JobId { get; init; }

    public required string MimeType { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>The path recorded when the bytes were written, not one recomputed from today's data directory.</summary>
    public required string StoragePath { get; init; }
}
