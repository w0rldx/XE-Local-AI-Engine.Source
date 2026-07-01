namespace XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     Rebuildable embedding index for a <see cref="KnowledgeDocumentChunk" /> (managed cosine-search path). One row per
///     chunk; the <see cref="Embedding" /> BLOB is a little-endian <c>float32</c> array. Vectors are always compared only
///     within a single <see cref="EmbeddingModel" /> (same-dimension, same-model), so the model id is stored alongside the
///     blob as the search filter key. Fully derivable from <c>knowledge_document_chunks</c> by re-embedding.
/// </summary>
internal sealed record class KnowledgeChunkVector
{
    /// <summary>Owning chunk. Primary key and foreign key to <c>knowledge_document_chunks.chunk_id</c> (cascade delete).</summary>
    public Guid ChunkId { get; set; }

    /// <summary>Owning document. Foreign-keyed to <c>knowledge_documents</c> with cascade delete; enables fast document-scoped purge.</summary>
    public Guid DocumentId { get; set; }

    /// <summary>Vector dimensionality (e.g. 768); guards against comparing vectors built at a different dimension.</summary>
    public int Dim { get; set; }

    /// <summary>Little-endian <c>float32[Dim]</c> embedding bytes (via <c>MemoryMarshal.AsBytes</c>).</summary>
    public byte[] Embedding { get; set; } = [];

    /// <summary>Embedding model id that produced this vector; the search compares only rows where this equals the current model.</summary>
    public string EmbeddingModel { get; set; } = string.Empty;
}
