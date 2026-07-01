namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Lifecycle state of a knowledge-base document as it advances through the ingestion pipeline
///     (extract → chunk → embed → index). Persisted as the enum name on the <c>knowledge_documents.status</c> column
///     and surfaced to the knowledge-base management UI. This is the canonical definition owned by the persistence lane;
///     the ingestion service produces these values.
/// </summary>
public enum KnowledgeDocumentStatus
{
    /// <summary>The document row and encrypted blob exist, but ingestion has not started.</summary>
    Pending,

    /// <summary>Structured text extraction is running.</summary>
    Extracting,

    /// <summary>The extracted document is being split into ordered chunks.</summary>
    Chunking,

    /// <summary>Chunk embeddings are being generated and written to the vector index.</summary>
    Embedding,

    /// <summary>Ingestion completed; chunks, vectors, and the FTS index are queryable.</summary>
    Indexed,

    /// <summary>A pipeline stage threw or produced nothing usable; see the content-free failure reason.</summary>
    Failed
}
