namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     A hybrid knowledge-base search request.
/// </summary>
public sealed class KnowledgeSearchRequest
{
    /// <summary>The untrusted user query. Escaped before it reaches FTS and embedded for the vector arm.</summary>
    public required string Query { get; init; }

    /// <summary>Maximum number of fused results to return.</summary>
    public required int Limit { get; init; }

    /// <summary>Optional scope: restrict the search to a single document.</summary>
    public Guid? DocumentId { get; init; }

    /// <summary>When true, each hit's content is expanded with its surrounding neighbor chunks.</summary>
    public bool ExpandNeighbors { get; init; }

    /// <summary>Collection/project namespace. Defaults to the backwards-compatible node corpus.</summary>
    public string CollectionId { get; init; } = KnowledgeCollectionScope.DefaultId;
}

/// <summary>The structured result of a hybrid knowledge-base search.</summary>
public sealed class KnowledgeSearchResult
{
    /// <summary>The fused, hydrated hits ordered by descending fused score.</summary>
    public required IReadOnlyList<KnowledgeSearchHit> Results { get; init; }
}

/// <summary>
///     One hydrated search hit. <see cref="Title" /> and <see cref="Section" /> are derived from the non-sensitive
///     <c>heading_path</c>/<c>storage_path</c> so a result never exposes the encrypted original file name.
/// </summary>
public sealed class KnowledgeSearchHit
{
    /// <summary>Owning document identifier.</summary>
    public required Guid DocumentId { get; init; }

    /// <summary>Matched chunk identifier.</summary>
    public required Guid ChunkId { get; init; }

    /// <summary>Non-sensitive display title (root heading segment, else the server-generated storage reference).</summary>
    public required string Title { get; init; }

    /// <summary>The chunk's heading trail (<c>heading_path</c>), or <see langword="null" /> when there is none.</summary>
    public required string? Section { get; init; }

    /// <summary>The matched chunk content, optionally joined with neighbor chunks when expansion was requested.</summary>
    public required string Content { get; init; }

    /// <summary>Constant provenance tag for this retrieval surface.</summary>
    public required string Source { get; init; }

    /// <summary>Relevance score, higher is more relevant: the fused Reciprocal Rank Fusion score, or — when the reranker is enabled and succeeds — the cross-encoder relevance score the hit was reordered by.</summary>
    public required double Score { get; init; }

    /// <summary>Global order of the matched chunk within the document.</summary>
    public required int ChunkIndex { get; init; }

    /// <summary>
    ///     The owning document's current catalog/pipeline status at retrieval time. A hit only ever exists because the
    ///     document has queryable chunks, so a non-<see cref="KnowledgeDocumentStatus.Indexed" /> status means those
    ///     chunks are the last successfully-indexed projection while a re-index is pending/running/failed.
    /// </summary>
    public required KnowledgeDocumentStatus DocumentStatus { get; init; }

    /// <summary>
    ///     True when <see cref="DocumentStatus" /> is not <see cref="KnowledgeDocumentStatus.Indexed" /> — i.e. the
    ///     content is a last-known-good projection served while the document is mid-reindex or its latest re-ingest
    ///     failed. The retrieval surfaces (agent tool output, REST/citation payloads, UI) disclose this so a consumer
    ///     never treats potentially-stale content as freshly indexed.
    /// </summary>
    public required bool ServingLastKnownGood { get; init; }

    public string CollectionId { get; init; } = KnowledgeCollectionScope.DefaultId;

    public string? SourcePath { get; init; }

    public string ContentKind { get; init; } = "text";

    public string? Language { get; init; }

    public string? Symbol { get; init; }

    public int? PageNumber { get; init; }

    public int StartOffset { get; init; }

    public int EndOffset { get; init; }
}
