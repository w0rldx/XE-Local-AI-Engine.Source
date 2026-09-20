namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Lexical retrieval arm of the hybrid search pipeline: an FTS5 <c>MATCH</c> over <c>chunk_fts</c> returning the
///     best-matching chunks ranked by BM25.
/// </summary>
/// <remarks>
///     <c>chunk_fts</c> is the external-content index mirroring <c>knowledge_document_chunks.content</c>. Scoped: reads
///     through the request-scoped <see cref="Persistence.NodeChatDbContext" /> connection. The untrusted query is always
///     escaped before it reaches <c>MATCH</c>, so operator characters can never inject FTS query syntax.
/// </remarks>
public interface IFtsSearch
{
    /// <summary>Returns up to <paramref name="limit" /> chunks matching <paramref name="query" />, best first.</summary>
    /// <remarks>
    ///     A blank query, or one with no indexable terms, yields an empty list rather than an error. A non-null
    ///     <paramref name="documentId" /> is pushed into the SQL <c>WHERE</c> clause, so a scoped search over a large
    ///     corpus cannot miss the target document's chunks.
    /// </remarks>
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
public sealed class FtsSearchHit
{
    /// <summary>Stable chunk identifier.</summary>
    public required Guid ChunkId { get; init; }

    /// <summary>Owning document identifier.</summary>
    public required Guid DocumentId { get; init; }

    /// <summary>The FTS5 BM25 relevance score; more-negative values rank higher.</summary>
    public required double Bm25Score { get; init; }
}
