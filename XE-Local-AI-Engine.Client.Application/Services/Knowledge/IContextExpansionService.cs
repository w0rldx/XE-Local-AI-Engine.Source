namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Runtime.InteropServices;

/// <summary>
///     Expands a single matched chunk into its surrounding context by fetching neighbor chunks with a nearby
///     <c>chunk_index</c> in the same document, so a fact that straddles a chunk boundary is returned with the parent
///     passage around it. Scoped: reads through the request-scoped <see cref="Persistence.NodeChatDbContext" /> connection.
/// </summary>
public interface IContextExpansionService
{
    /// <summary>
    ///     Returns the matched chunk together with its neighbors whose <c>chunk_index</c> is within
    ///     <paramref name="window" /> of <paramref name="chunkIndex" />, ordered by <c>chunk_index</c> ascending. A window
    ///     of zero returns just the matched chunk.
    /// </summary>
    Task<IReadOnlyList<KnowledgeNeighborChunk>> ExpandAsync(Guid documentId,
        int chunkIndex,
        int window,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Batched form of <see cref="ExpandAsync" /> for a whole set of matched chunks at once: returns, index-for-index
    ///     with <paramref name="anchors" />, each anchor's neighbor window (same rows, same ascending order as calling
    ///     <see cref="ExpandAsync" /> per anchor). Collapses the per-hit round trips to one query per distinct document, so
    ///     a top-k that shares a document is expanded without an N+1 fan-out. Anchors in the same document with overlapping
    ///     windows each still receive their own full window (identical to the per-anchor path). An empty anchor list yields
    ///     an empty result.
    /// </summary>
    Task<IReadOnlyList<IReadOnlyList<KnowledgeNeighborChunk>>> ExpandBatchAsync(IReadOnlyList<KnowledgeNeighborAnchor> anchors,
        int window,
        CancellationToken cancellationToken);
}

/// <summary>A single chunk to expand: the document and the matched chunk's global index within it.</summary>
/// <param name="DocumentId">The document the matched chunk belongs to.</param>
/// <param name="ChunkIndex">Global order of the matched chunk within the document.</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct KnowledgeNeighborAnchor(Guid DocumentId, int ChunkIndex);

/// <summary>A chunk returned by context expansion, in document order.</summary>
/// <param name="ChunkId">Stable chunk identifier.</param>
/// <param name="ChunkIndex">Global order of this chunk within the document.</param>
/// <param name="Content">Plaintext chunk content.</param>
/// <param name="HeadingPath">The "H1 &gt; H2" heading trail, or <see langword="null" /> when there is none.</param>
public sealed record KnowledgeNeighborChunk(Guid ChunkId, int ChunkIndex, string Content, string? HeadingPath);
