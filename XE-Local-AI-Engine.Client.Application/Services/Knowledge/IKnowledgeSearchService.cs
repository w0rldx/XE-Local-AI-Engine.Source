namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Orchestrates hybrid knowledge-base retrieval and is the public seam a downstream tool or endpoint calls.
/// </summary>
/// <remarks>
///     Embeds the query with the current model, runs the lexical FTS arm and the model-scoped semantic vector arm, fuses
///     their rankings with Reciprocal Rank Fusion, hydrates the top results, and optionally expands each hit with
///     surrounding context. Scoped: it drives the scoped FTS/vector/expansion services through the request-scoped
///     connection.
/// </remarks>
public interface IKnowledgeSearchService
{
    /// <summary>Runs a hybrid search and returns the fused, hydrated results.</summary>
    Task<KnowledgeSearchResult> SearchAsync(KnowledgeSearchRequest request, CancellationToken cancellationToken);
}
