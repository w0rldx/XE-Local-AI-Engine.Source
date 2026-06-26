namespace XE_Local_AI_Engine.Client.Services.DocumentIngestion;

/// <summary>
///     Durable per-conversation store for uploaded files. Bytes and cached extracted Markdown live encrypted on disk
///     under <c>INodeDataDirectory.Root/uploaded-files/conversations/{conversationId}/</c>; metadata (with an encrypted
///     display name) lives in the <c>conversation_uploaded_files</c> table. The storage path is server-generated, so no
///     client-supplied string ever forms a filesystem path.
/// </summary>
public interface IConversationUploadedFileStore
{
    /// <summary>Persists one uploaded file (encrypted bytes + optional encrypted extracted Markdown + metadata row).</summary>
    Task<ConversationUploadedFileInfo> AddAsync(ConversationUploadedFileInput input, CancellationToken cancellationToken);

    /// <summary>Lists the metadata for every file attached to the conversation, oldest first.</summary>
    Task<IReadOnlyList<ConversationUploadedFileInfo>> ListAsync(Guid conversationId, CancellationToken cancellationToken);

    /// <summary>Decrypts and returns the cached extracted Markdown for one file, or null when none was cached.</summary>
    Task<string?> ReadExtractedMarkdownAsync(Guid conversationId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>Removes one file's metadata row plus its on-disk bytes and extracted Markdown. Returns whether a row existed.</summary>
    Task<bool> DeleteAsync(Guid conversationId, Guid fileId, CancellationToken cancellationToken);

    /// <summary>Removes the conversation's on-disk upload directory. The metadata rows are removed by the caller's delete path.</summary>
    Task DeleteAllForConversationAsync(Guid conversationId, CancellationToken cancellationToken);

    /// <summary>
    ///     Decrypts each cached <c>.md</c> for the conversation into a fresh temp directory and returns a snapshot whose
    ///     disposal removes that directory. Used by the AgentHome staging step to copy attachments into the sandbox.
    /// </summary>
    Task<IConversationStagingSnapshot> CreateStagingSnapshotAsync(Guid conversationId, CancellationToken cancellationToken);
}
