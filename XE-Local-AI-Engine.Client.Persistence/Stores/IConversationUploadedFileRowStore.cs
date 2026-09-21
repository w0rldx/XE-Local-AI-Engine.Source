namespace XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     Persistence boundary for the <c>conversation_uploaded_files</c> metadata rows.
/// </summary>
/// <remarks>
///     Only the rows: the durable bytes and the cached extracted Markdown are too large for the encrypted column path
///     and live on disk, owned by the application-layer uploaded-file store that calls this one. The display name is
///     encrypted at rest with the same protector and AAD the EF interceptors use, so callers hand over and receive
///     plaintext and never see the ciphertext. Consumed through a fresh DI scope per operation.
/// </remarks>
public interface IConversationUploadedFileRowStore
{
    /// <summary>Inserts one metadata row, encrypting <see cref="ConversationUploadedFileRow.OriginalFileName" /> at rest.</summary>
    Task InsertAsync(ConversationUploadedFileRow row, string storagePath, CancellationToken cancellationToken);

    /// <summary>Lists the conversation's rows oldest first, with the display name decrypted.</summary>
    Task<IReadOnlyList<ConversationUploadedFileRow>> ListAsync(Guid conversationId, CancellationToken cancellationToken);

    /// <summary>
    ///     Deletes one row and returns its stored extension, or <see langword="null" /> when there was no row to delete.
    /// </summary>
    /// <remarks>
    ///     The extension is what lets the caller name the on-disk blob it must unlink next. A row whose extension column
    ///     is NULL reads as "nothing to delete" exactly as it did before this moved behind a store: the caller cannot
    ///     locate the blob without it, so reporting the row as deleted would strand the bytes.
    /// </remarks>
    Task<string?> DeleteAsync(Guid conversationId, Guid fileId, CancellationToken cancellationToken);
}

/// <summary>One <c>conversation_uploaded_files</c> row, with the display name in plaintext on both sides of the store.</summary>
/// <remarks>
///     <c>ExtractionStatus</c> is the persisted enum NAME rather than the enum: the extraction vocabulary belongs to the
///     application layer's ingestion pipeline, and the column has always stored the name.
/// </remarks>
public sealed class ConversationUploadedFileRow
{
    public required Guid FileId { get; init; }

    public required Guid ConversationId { get; init; }

    public required string OriginalFileName { get; init; }

    public required string MimeType { get; init; }

    public required string Extension { get; init; }

    public required long SizeBytes { get; init; }

    public required string ExtractionStatus { get; init; }

    public required int? ExtractedChars { get; init; }

    public required long CreatedAtUtc { get; init; }
}
