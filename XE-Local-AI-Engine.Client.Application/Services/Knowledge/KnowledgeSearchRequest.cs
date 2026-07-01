namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     A hybrid knowledge-base search request.
/// </summary>
/// <param name="Query">The untrusted user query. Escaped before it reaches FTS and embedded for the vector arm.</param>
/// <param name="Limit">Maximum number of fused results to return.</param>
/// <param name="DocumentId">Optional scope: restrict the search to a single document.</param>
/// <param name="ExpandNeighbors">When true, each hit's content is expanded with its surrounding neighbor chunks.</param>
public sealed record KnowledgeSearchRequest(string Query, int Limit, Guid? DocumentId = null, bool ExpandNeighbors = false);

/// <summary>The structured result of a hybrid knowledge-base search.</summary>
/// <param name="Results">The fused, hydrated hits ordered by descending fused score.</param>
public sealed record KnowledgeSearchResult(IReadOnlyList<KnowledgeSearchHit> Results);

/// <summary>
///     One hydrated search hit. <see cref="Title" /> and <see cref="Section" /> are derived from the non-sensitive
///     <c>heading_path</c>/<c>storage_path</c> so a result never exposes the encrypted original file name.
/// </summary>
/// <param name="DocumentId">Owning document identifier.</param>
/// <param name="ChunkId">Matched chunk identifier.</param>
/// <param name="Title">Non-sensitive display title (root heading segment, else the server-generated storage reference).</param>
/// <param name="Section">The chunk's heading trail (<c>heading_path</c>), or <see langword="null" /> when there is none.</param>
/// <param name="Content">The matched chunk content, optionally joined with neighbor chunks when expansion was requested.</param>
/// <param name="Source">Constant provenance tag for this retrieval surface.</param>
/// <param name="Score">The fused Reciprocal Rank Fusion score (higher is more relevant).</param>
/// <param name="ChunkIndex">Global order of the matched chunk within the document.</param>
public sealed record KnowledgeSearchHit(
    Guid DocumentId,
    Guid ChunkId,
    string Title,
    string? Section,
    string Content,
    string Source,
    double Score,
    int ChunkIndex);
