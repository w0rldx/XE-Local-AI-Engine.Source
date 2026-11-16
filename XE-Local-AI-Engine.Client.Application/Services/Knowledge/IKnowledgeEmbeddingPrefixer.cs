namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Applies the input format the resolved embedding model was trained with, such as nomic's <c>search_document: </c> /
///     <c>search_query: </c> prefixes, so the model can tell a stored passage from a search query.
/// </summary>
/// <remarks>
///     The prefix is prepended only to the text handed to the embedding generator; it is never persisted into a chunk's
///     stored content. The search path reuses <see cref="ForQuery" /> with the same resolution so the query vector is built
///     with the matching format.
/// </remarks>
public interface IKnowledgeEmbeddingPrefixer
{
    /// <summary>Formats a chunk's plaintext as a document for the resolved model's ingestion-time embedding.</summary>
    string ForDocument(EmbeddingModelResolution resolution, string content);

    /// <summary>Formats a search query for the resolved model's retrieval-time embedding.</summary>
    string ForQuery(EmbeddingModelResolution resolution, string query);
}
