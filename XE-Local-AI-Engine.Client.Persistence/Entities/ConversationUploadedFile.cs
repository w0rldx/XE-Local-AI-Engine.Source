namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     A single file a user attached to a conversation. The durable bytes and the cached extracted Markdown live on disk
///     under <c>INodeDataDirectory.Root/uploaded-files/conversations/{conversation_id}/</c> (too large for the encrypted
///     column path); this row holds only the metadata plus the encrypted display name.
/// </summary>
internal sealed record class ConversationUploadedFile
{
    public Guid FileId { get; set; }

    /// <summary>Owning conversation. Foreign-keyed to <c>conversations</c> with cascade delete (see configuration).</summary>
    public Guid ConversationId { get; set; }

    /// <summary>
    ///     UTF-8 display-name bytes. Plaintext while tracked in memory; encrypted at rest by
    ///     <see cref="NodeEncryptionSaveChangesInterceptor" /> and decrypted by
    ///     <see cref="NodeEncryptionMaterializationInterceptor" /> using AAD column name <c>original_file_name</c>.
    ///     The store writes/reads this column over the raw-SQL path via the matching
    ///     <c>NodeChatDbContext.EncryptUploadedFileName</c>/<c>DecryptUploadedFileName</c> helpers (same protector + AAD).
    /// </summary>
    public byte[] OriginalFileName { get; set; } = [];

    public string MimeType { get; set; } = string.Empty;

    /// <summary>Normalized lowercase extension (with leading dot) that drives extraction dispatch.</summary>
    public string Extension { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>The <c>DocumentExtractionStatus</c> enum name (<c>Pending|Extracted|Unsupported|Failed</c>).</summary>
    public string ExtractionStatus { get; set; } = string.Empty;

    /// <summary>Extracted Markdown character count, used by the plain-chat budget/UI. Null until extraction succeeds.</summary>
    public int? ExtractedChars { get; set; }

    /// <summary>Server-generated relative path under the upload root (<c>{conversation_id}/{file_id}{ext}</c>); never user-controlled.</summary>
    public string StoragePath { get; set; } = string.Empty;

    public long CreatedAtUtc { get; set; }
}
