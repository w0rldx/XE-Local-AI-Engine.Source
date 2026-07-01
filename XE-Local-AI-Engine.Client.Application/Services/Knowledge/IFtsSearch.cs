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
    ///     a query with no indexable terms yields an empty list rather than an error.
    /// </summary>
    Task<IReadOnlyList<FtsSearchHit>> SearchAsync(string query, int limit, CancellationToken cancellationToken);
}

/// <summary>One lexical match: the chunk, its owning document, and its BM25 score (lower is a better match in FTS5).</summary>
/// <param name="ChunkId">Stable chunk identifier.</param>
/// <param name="DocumentId">Owning document identifier.</param>
/// <param name="Bm25Score">The FTS5 BM25 relevance score; more-negative values rank higher.</param>
public sealed record FtsSearchHit(Guid ChunkId, Guid DocumentId, double Bm25Score);
