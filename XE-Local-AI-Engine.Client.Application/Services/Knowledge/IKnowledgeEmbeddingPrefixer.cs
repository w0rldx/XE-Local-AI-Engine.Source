namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Applies the task-instruction prefix that asymmetric embedding models (e.g. <c>nomic-embed-text</c>) require to
///     distinguish a stored passage from a search query. The prefix is prepended only to the text handed to the embedding
///     generator; it is never persisted into a chunk's stored content. The search path reuses <see cref="ForQuery" /> so the query
///     vector is built with the matching intent.
/// </summary>
public interface IKnowledgeEmbeddingPrefixer
{
    /// <summary>Prefixes a chunk's plaintext with the document/passage intent for ingestion-time embedding.</summary>
    string ForDocument(string content);

    /// <summary>Prefixes a search query with the query intent for retrieval-time embedding.</summary>
    string ForQuery(string query);
}
