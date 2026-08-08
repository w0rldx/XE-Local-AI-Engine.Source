namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Source-of-truth retrieval unit — a plaintext chunk of a <see cref="KnowledgeDocument" />. The FTS5 external-content
///     index (<c>chunk_fts</c>, added by the raw-SQL migration) keys on <see cref="Rowid" />, so this table intentionally
///     uses an explicit <c>INTEGER PRIMARY KEY</c> rowid alias rather than the <see cref="ChunkId" /> GUID: a SQLite table
///     has exactly one primary key, and an implicit rowid would be reassigned by <c>VACUUM</c>, silently breaking the
///     FTS↔content alignment. <see cref="ChunkId" /> is the stable public identifier and a UNIQUE alternate key that the
///     vector index foreign-keys to.
/// </summary>
internal sealed record class KnowledgeDocumentChunk
{
    /// <summary>
    ///     Explicit <c>INTEGER PRIMARY KEY</c> surrogate — the SQLite rowid alias FTS5 external content aligns on
    ///     (<c>content_rowid='rowid'</c>). Stable across <c>VACUUM</c> because it is an aliased, not implicit, rowid.
    ///     Database-generated on insert.
    /// </summary>
    public long Rowid { get; set; }

    /// <summary>Stable public chunk identifier. UNIQUE alternate key (not the primary key); the vector index references it.</summary>
    public Guid ChunkId { get; set; }

    /// <summary>Owning document. Foreign-keyed to <c>knowledge_documents</c> with cascade delete (see configuration).</summary>
    public Guid DocumentId { get; set; }

    /// <summary>Owning section, when the chunk falls under one. Foreign-keyed to <c>knowledge_document_sections</c> with set-null on delete.</summary>
    public Guid? SectionId { get; set; }

    /// <summary>Global order of this chunk within the document; the neighbor-expansion key.</summary>
    public int ChunkIndex { get; set; }

    /// <summary>Plaintext chunk content — the searched text (mirrored into the FTS index by trigger).</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>Token count of <see cref="Content" />, for observability and budgeting.</summary>
    public int TokenCount { get; set; }

    /// <summary>Denormalized "H1 &gt; H2" heading trail for the retrieval result's section field; null when unknown.</summary>
    public string? HeadingPath { get; set; }
}
