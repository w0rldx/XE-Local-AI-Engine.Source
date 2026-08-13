namespace XE_Local_AI_Engine.Client.Endpoints.Knowledge.V1;

using XE_Local_AI_Engine.Client.Services.Knowledge;

// ---------------------------------------------------------------------------
// Request DTOs
//
// These mirror the established endpoint-DTO style in this project (sealed class + required/init, e.g.
// SchedulerEndpointDtos / ConversationUploadEndpointDtos) rather than positional records: the multipart upload binding
// and the route/query binding are proven against this shape, and keeping the whole file consistent avoids mixing styles.
// ---------------------------------------------------------------------------

/// <summary>
///     Route binding for the multipart knowledge-document upload. The file rides the multipart form; the typed
///     <see cref="File" /> property exists so FastEndpoints documents a <c>multipart/form-data</c> body in OpenAPI (the
///     generated hey-api client then serializes the upload as form-data rather than JSON). The handler still reads the
///     form-file collection directly, so the binding tolerates whichever multipart field name the client chooses.
/// </summary>
public sealed class UploadKnowledgeDocumentRequest
{
    public IFormFile? File { get; init; }

    public string CollectionId { get; init; } = KnowledgeCollectionScope.DefaultId;
}

/// <summary>Optional collection filter for the management document list.</summary>
public sealed class ListKnowledgeDocumentsRequest
{
    public string? CollectionId { get; init; }
}

/// <summary>Route-only request for <c>GET/DELETE knowledge-base/documents/{documentId}</c> and the reindex action.</summary>
public sealed class KnowledgeDocumentRouteRequest
{
    public Guid DocumentId { get; init; }
}

/// <summary>Body for <c>POST knowledge-base/search</c>. The query is untrusted and escaped/embedded by the search service.</summary>
public sealed class SearchKnowledgeRequest
{
    public required string Query { get; init; }

    /// <summary>Maximum number of fused results to return. Bounded server-side to a safe range.</summary>
    public int Limit { get; init; } = 10;

    /// <summary>Optional scope: restrict the search to a single document.</summary>
    public Guid? DocumentId { get; init; }

    /// <summary>When true, each hit's content is expanded with its surrounding neighbor chunks.</summary>
    public bool ExpandNeighbors { get; init; }

    /// <summary>Collection/project namespace searched by both lexical and dense retrieval.</summary>
    public string CollectionId { get; init; } = KnowledgeCollectionScope.DefaultId;
}

/// <summary>Imports one previously registered local Git repository into a knowledge collection.</summary>
public sealed class ImportKnowledgeRepositoryRequest
{
    public required Guid SelectedFolderId { get; init; }

    public string? CollectionId { get; init; }
}

public sealed class ImportKnowledgeRepositoryResponse
{
    public required string CollectionId { get; init; }

    public required int DiscoveredFiles { get; init; }

    public required int AddedDocuments { get; init; }

    public required int UpdatedDocuments { get; init; }

    public required int RemovedDocuments { get; init; }

    public required int DeduplicatedDocuments { get; init; }

    public required int EnqueuedDocuments { get; init; }

    public required int SkippedFiles { get; init; }

    public required bool QueueCapacityReached { get; init; }
}

// ---------------------------------------------------------------------------
// Response DTOs
// ---------------------------------------------------------------------------

/// <summary>
///     Result of a multipart upload. <see cref="Deduplicated" /> is true when an identical file (same content hash)
///     already existed — in that case <see cref="DocumentId" /> is the pre-existing document's id and no new ingestion
///     was enqueued. <see cref="Status" /> is the document's current pipeline status. Enums serialize as their string
///     names via the globally registered converter.
/// </summary>
public sealed class UploadKnowledgeDocumentResponse
{
    public required Guid DocumentId { get; init; }

    public required KnowledgeDocumentStatus Status { get; init; }

    public required bool Deduplicated { get; init; }
}

/// <summary>
///     Management summary of one knowledge-base document. <see cref="DisplayName" /> is the decrypted original file name
///     (owner-only, over this authenticated surface). <see cref="StaleModel" /> is true when the document was embedded
///     with a model other than the currently configured one, so the UI can offer a reindex.
/// </summary>
public sealed class KnowledgeDocumentResponse
{
    public required Guid DocumentId { get; init; }

    public required string DisplayName { get; init; }

    public required KnowledgeDocumentStatus Status { get; init; }

    public string? FailureReason { get; init; }

    public required int ChunkCount { get; init; }

    public required string EmbeddingModel { get; init; }

    public required bool StaleModel { get; init; }

    public required long SizeBytes { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required string CollectionId { get; init; }

    public string? SourcePath { get; init; }

    public required string SourceKind { get; init; }
}

/// <summary>Response envelope for <c>GET knowledge-base/documents</c>.</summary>
public sealed class ListKnowledgeDocumentsResponse
{
    public required IReadOnlyList<KnowledgeDocumentResponse> Items { get; init; }
}

/// <summary>One chunk of a document, for the detail drawer.</summary>
public sealed class KnowledgeDocumentChunkResponse
{
    public required int ChunkIndex { get; init; }

    public string? HeadingPath { get; init; }

    public required string Content { get; init; }

    public int? PageNumber { get; init; }

    public required int StartOffset { get; init; }

    public required int EndOffset { get; init; }

    public required string ContentKind { get; init; }

    public string? SourcePath { get; init; }

    public string? Language { get; init; }

    public string? Symbol { get; init; }
}

/// <summary>Full detail of one document plus its ordered chunks. Response for <c>GET knowledge-base/documents/{documentId}</c>.</summary>
public sealed class KnowledgeDocumentDetailResponse
{
    public required Guid DocumentId { get; init; }

    public required string DisplayName { get; init; }

    public required KnowledgeDocumentStatus Status { get; init; }

    public string? FailureReason { get; init; }

    public required int ChunkCount { get; init; }

    public required string EmbeddingModel { get; init; }

    public required bool StaleModel { get; init; }

    public required long SizeBytes { get; init; }

    public required long CreatedAtUtc { get; init; }

    public required long UpdatedAtUtc { get; init; }

    public required string CollectionId { get; init; }

    public string? SourcePath { get; init; }

    public required string SourceKind { get; init; }

    public required IReadOnlyList<KnowledgeDocumentChunkResponse> Chunks { get; init; }
}

/// <summary>One hydrated search hit. Titles/sections derive from non-sensitive heading/storage refs, never the file name.</summary>
public sealed class KnowledgeSearchHitResponse
{
    public required Guid DocumentId { get; init; }

    public required Guid ChunkId { get; init; }

    public required string Title { get; init; }

    public string? Section { get; init; }

    public required string Content { get; init; }

    public required string Source { get; init; }

    public required double Score { get; init; }

    public required int ChunkIndex { get; init; }

    /// <summary>The owning document's catalog status at retrieval time (enum name via the global converter).</summary>
    public required KnowledgeDocumentStatus DocumentStatus { get; init; }

    /// <summary>
    ///     True when the hit is a last-known-good projection served while the document is mid-reindex or its latest
    ///     re-ingest failed (i.e. <see cref="DocumentStatus" /> is not <c>Indexed</c>). The UI badges these hits.
    /// </summary>
    public required bool ServingLastKnownGood { get; init; }

    public required string CollectionId { get; init; }

    public string? SourcePath { get; init; }

    public required string ContentKind { get; init; }

    public string? Language { get; init; }

    public string? Symbol { get; init; }

    public int? PageNumber { get; init; }

    public required int StartOffset { get; init; }

    public required int EndOffset { get; init; }
}

/// <summary>Response envelope for <c>POST knowledge-base/search</c>.</summary>
public sealed class SearchKnowledgeResponse
{
    public required IReadOnlyList<KnowledgeSearchHitResponse> Results { get; init; }
}

/// <summary>Result of a corpus-wide reindex: how many stale-model documents were reset to Pending and enqueued.</summary>
public sealed class ReindexCorpusResponse
{
    public required int EnqueuedCount { get; init; }
}

/// <summary>
///     Result of the one-click recommended-reranker download. Carries the same core identity the GGUF download trigger
///     returns (<see cref="ModelName" /> + <see cref="AlreadyInFlight" />) plus the recommended descriptor
///     (<see cref="RepoId" />/<see cref="Quant" />) and an <see cref="AlreadyInstalled" /> flag so the UI can show what is
///     being fetched and give a friendly no-op when the model is already present. Exactly one of the three states holds:
///     already installed (no download started), already in flight (rejoined an existing download), or a fresh download
///     started — the download runs in the background and progress streams over the GGUF download hub, keyed by
///     <see cref="ModelName" />.
/// </summary>
public sealed class DownloadRecommendedRerankerResponse
{
    /// <summary>Canonical <c>{repoId}:{quant}</c> model name to track the download by and to select as the reranker.</summary>
    public required string ModelName { get; init; }

    /// <summary>Recommended Hugging Face repository id.</summary>
    public required string RepoId { get; init; }

    /// <summary>Pinned quant of the recommended reranker.</summary>
    public required string Quant { get; init; }

    /// <summary><c>true</c> when the recommended reranker is already installed — no download was started.</summary>
    public required bool AlreadyInstalled { get; init; }

    /// <summary><c>true</c> when an existing download for the same model was rejoined instead of a new one started.</summary>
    public required bool AlreadyInFlight { get; init; }
}

/// <summary>
///     Result of the one-click recommended-embedding-model download. Shape-identical to
///     <see cref="DownloadRecommendedRerankerResponse" /> so the two buttons share one UI idiom, with one semantic
///     difference worth knowing: <see cref="AlreadyInstalled" /> reports whether the node can embed AT ALL, so
///     <see cref="ModelName" /> may name a different embedding model the operator already had rather than the
///     recommended one. <see cref="RepoId" />/<see cref="Quant" /> always describe the recommendation itself.
/// </summary>
public sealed class DownloadRecommendedEmbeddingResponse
{
    /// <summary>The embedding model that will actually be used — the recommended one, or an already-installed equivalent.</summary>
    public required string ModelName { get; init; }

    /// <summary>Recommended Hugging Face repository id.</summary>
    public required string RepoId { get; init; }

    /// <summary>Pinned quant of the recommended embedding model.</summary>
    public required string Quant { get; init; }

    /// <summary><c>true</c> when a usable embedding model is already installed — no download was started.</summary>
    public required bool AlreadyInstalled { get; init; }

    /// <summary><c>true</c> when an existing download for the same model was rejoined instead of a new one started.</summary>
    public required bool AlreadyInFlight { get; init; }
}
