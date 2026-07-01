namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     One uploaded knowledge-base document — the source of truth for a corpus file. The durable raw bytes live encrypted
///     on disk under <c>INodeDataDirectory.Root/knowledge-base/documents/</c> (too large for the encrypted column path);
///     this row holds only the metadata plus the encrypted display name. Chunk text, section structure, and embedding
///     vectors are rebuildable projections keyed off <see cref="DocumentId" /> in the sibling knowledge tables.
/// </summary>
internal sealed record class KnowledgeDocument
{
    public Guid DocumentId { get; set; }

    /// <summary>
    ///     UTF-8 display-name bytes. Encrypted at rest via the raw-SQL store path using
    ///     <c>NodeChatDbContext.EncryptKnowledgeFileName</c>/<c>DecryptKnowledgeFileName</c> (AAD column name
    ///     <c>original_file_name</c>, bound to <c>(Guid.Empty, documentId)</c>). This column is display metadata only and
    ///     is never searched, so it is deliberately kept OUT of the node-encryption interceptor loops — all writes flow
    ///     through the store's raw-SQL path.
    /// </summary>
    public byte[] OriginalFileName { get; set; } = [];

    public string MimeType { get; set; } = string.Empty;

    /// <summary>Normalized lowercase extension (with leading dot) that drives extraction dispatch.</summary>
    public string Extension { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    /// <summary>SHA-256 hex of the raw bytes. Unique across the corpus so a duplicate upload is deduped, not re-ingested.</summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>Server-generated relative path to the encrypted blob; display-only, never used to open a file.</summary>
    public string StoragePath { get; set; } = string.Empty;

    /// <summary>The <c>KnowledgeDocumentStatus</c> enum name (<c>Pending|Extracting|Chunking|Embedding|Indexed|Failed</c>).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Actionable, content-free reason set when <see cref="Status" /> is <c>Failed</c>; null otherwise.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Number of persisted chunks; defaults to 0 and is backfilled when the document reaches <c>Indexed</c>.</summary>
    public int ChunkCount { get; set; }

    /// <summary>Embedding model id that built the vectors (e.g. <c>nomic-embed-text</c>); the search model-mismatch filter key.</summary>
    public string EmbeddingModel { get; set; } = string.Empty;

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }
}
