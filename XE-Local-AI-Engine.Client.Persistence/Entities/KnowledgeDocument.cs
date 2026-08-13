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

    /// <summary>Lightweight collection/project namespace. Existing documents are backfilled to <c>DEFAULT</c>.</summary>
    public string CollectionId { get; set; } = "DEFAULT";

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

    /// <summary>
    ///     SHA-256 hex of the raw bytes. Uploads dedupe by collection + hash; repository documents retain distinct path
    ///     provenance even when their bytes are identical.
    /// </summary>
    public string ContentHash { get; set; } = string.Empty;

    /// <summary>Server-generated relative path to the encrypted blob; display-only, never used to open a file.</summary>
    public string StoragePath { get; set; } = string.Empty;

    /// <summary>Optional non-secret path relative to a repository/project root. Null for ordinary file uploads.</summary>
    public string? SourcePath { get; set; }

    /// <summary>Deterministic source class such as <c>upload</c> or <c>repository</c>.</summary>
    public string SourceKind { get; set; } = "upload";

    /// <summary>Stable opaque identity of the owning source within its source class. Null for ordinary uploads.</summary>
    public string? SourceId { get; set; }

    /// <summary>The <c>KnowledgeDocumentStatus</c> enum name (<c>Pending|Extracting|Chunking|Embedding|Indexed|Failed</c>).</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Actionable, content-free reason set when <see cref="Status" /> is <c>Failed</c>; null otherwise.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Number of persisted chunks; defaults to 0 and is backfilled when the document reaches <c>Indexed</c>.</summary>
    public int ChunkCount { get; set; }

    /// <summary>Embedding model id that built the vectors (e.g. <c>nomic-embed-text</c>); the search model-mismatch filter key.</summary>
    public string EmbeddingModel { get; set; } = string.Empty;

    /// <summary>Canonical resolved-model + transform algorithm/version + width identity for the committed vectors.</summary>
    public string VectorIdentity { get; set; } = "legacy:unversioned";

    /// <summary>Committed vector width; duplicated from the canonical identity for defensive filtering and catalog checks.</summary>
    public int VectorDim { get; set; }

    public string ParserVersion { get; set; } = "legacy";

    public string ChunkerVersion { get; set; } = "legacy";

    public long CreatedAtUtc { get; set; }

    public long UpdatedAtUtc { get; set; }
}
