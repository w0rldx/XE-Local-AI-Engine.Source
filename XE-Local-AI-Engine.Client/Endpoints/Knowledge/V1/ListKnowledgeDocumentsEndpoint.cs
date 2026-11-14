namespace XE_Local_AI_Engine.Client.Endpoints.Knowledge.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Knowledge.V1.Validators;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Lists one page of knowledge-base documents as management summaries.
/// </summary>
/// <remarks>
///     Each summary carries the decrypted display name (owner-only, over this authenticated surface), pipeline status,
///     chunk count, embedding model and a computed stale-model flag — but never chunk content. The envelope also
///     carries the node-wide embedding-model resolution, which is the upload precondition the ingestion lane uses.
/// </remarks>
public sealed class ListKnowledgeDocumentsEndpoint : Endpoint<ListKnowledgeDocumentsRequest, ListKnowledgeDocumentsResponse>
{
    private readonly IKnowledgeDocumentCatalogService _catalogService;

    public ListKnowledgeDocumentsEndpoint(IKnowledgeDocumentCatalogService catalogService)
    {
        ArgumentNullException.ThrowIfNull(catalogService);
        _catalogService = catalogService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.KnowledgeBase.Documents);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListKnowledgeDocumentsRequest req, CancellationToken ct)
    {
        var listing = await _catalogService.ListWithEmbeddingStatusAsync(new KnowledgeDocumentListQuery
            {
                CollectionId = req.CollectionId,
                DocumentIds = req.DocumentIds,
                Limit = req.Limit ?? ListKnowledgeDocumentsRequestValidator.DefaultLimit,
                Offset = req.Offset ?? 0
            },
            ct);
        await Send.OkAsync(new ListKnowledgeDocumentsResponse
            {
                Items = [.. listing.Items.Select(ToResponse)],
                TotalCount = listing.TotalCount,
                HasStaleDocuments = listing.HasStaleDocuments,
                HasSearchableDocuments = listing.HasSearchableDocuments,
                EmbeddingModel = listing.Embedding.Name,
                EmbeddingModelAvailable = listing.Embedding.IsConfident
            },
            ct);
    }

    private static KnowledgeDocumentResponse ToResponse(KnowledgeDocumentSummary summary)
    {
        return new KnowledgeDocumentResponse
        {
            DocumentId = summary.DocumentId,
            DisplayName = summary.DisplayName,
            Status = summary.Status,
            FailureReason = summary.FailureReason,
            FailureReasonCode = KnowledgeFailureReasonCodes.CodeOf(summary.FailureReason),
            ChunkCount = summary.ChunkCount,
            EmbeddingModel = summary.EmbeddingModel,
            StaleModel = summary.StaleModel,
            SizeBytes = summary.SizeBytes,
            CreatedAtUtc = summary.CreatedAtUtc,
            CollectionId = summary.CollectionId,
            SourcePath = summary.SourcePath,
            SourceKind = summary.SourceKind
        };
    }
}
