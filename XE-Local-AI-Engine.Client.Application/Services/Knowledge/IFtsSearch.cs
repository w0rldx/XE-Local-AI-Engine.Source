namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Lexical retrieval arm of the hybrid search pipeline. Runs an FTS5 <c>MATCH</c> over <c>chunk_fts</c> (the
///     external-content full-text index mirroring <c>knowledge_document_chunks.content</c>) and returns the best-matching
///     chunks ranked by BM25. Scoped: it reads through the request-scoped <see cref="Persistence.NodeChatDbContext" />
///     connection. The untrusted query is always escaped before it reaches <c>MATCH</c> so operator characters can never
///     inject FTS query syntax.
/// </summary>
public interface IFtsSearch
{
    /// <summary>
    ///     Returns up to <paramref name="limit" /> chunks matching <paramref name="query" />, best first. A blank query or
    ///     a query with no indexable terms yields an empty list rather than an error. When <paramref name="documentId" />
    ///     is non-null, the scope is pushed into the SQL <c>WHERE</c> clause so a scoped search over a large corpus cannot
    ///     miss the target document's chunks.
    /// </summary>
    Task<IReadOnlyList<FtsSearchHit>> SearchAsync(string query, int limit, Guid? documentId, CancellationToken cancellationToken) =>
        SearchAsync(query, limit, documentId, KnowledgeCollectionScope.DefaultId, cancellationToken);

    /// <summary>Collection-scoped variant used by production retrieval. Implementations must enforce this boundary.</summary>
    Task<IReadOnlyList<FtsSearchHit>> SearchAsync(string query,
        int limit,
        Guid? documentId,
        string collectionId,
        CancellationToken cancellationToken);
}

/// <summary>One lexical match: the chunk, its owning document, and its BM25 score (lower is a better match in FTS5).</summary>
/// <param name="ChunkId">Stable chunk identifier.</param>
/// <param name="DocumentId">Owning document identifier.</param>
/// <param name="Bm25Score">The FTS5 BM25 relevance score; more-negative values rank higher.</param>
public sealed record FtsSearchHit(Guid ChunkId, Guid DocumentId, double Bm25Score);
