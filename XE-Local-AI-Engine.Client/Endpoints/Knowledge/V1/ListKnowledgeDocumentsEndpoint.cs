namespace XE_Local_AI_Engine.Client.Endpoints.Knowledge.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     FastEndpoints handler for listing every knowledge-base document (GET). Returns management summaries — including the
///     decrypted display name (owner-only, over this authenticated surface), pipeline status, chunk count, embedding
///     model, and a computed stale-model flag — but never chunk content.
/// </summary>
public sealed class ListKnowledgeDocumentsEndpoint(IKnowledgeDocumentCatalogService catalogService)
    : Endpoint<ListKnowledgeDocumentsRequest, ListKnowledgeDocumentsResponse>
{
    private readonly IKnowledgeDocumentCatalogService _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));

    public override void Configure()
    {
        Get(LocalApiRoutes.KnowledgeBase.Documents);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(ListKnowledgeDocumentsRequest req, CancellationToken ct)
    {
        var documents = string.IsNullOrWhiteSpace(req.CollectionId)
            ? await _catalogService.ListAsync(ct).ConfigureAwait(false)
            : await _catalogService.ListAsync(req.CollectionId, ct).ConfigureAwait(false);
        await Send.OkAsync(new ListKnowledgeDocumentsResponse
            {
                Items = [.. documents.Select(ToResponse)]
            },
            ct).ConfigureAwait(false);
    }

    private static KnowledgeDocumentResponse ToResponse(KnowledgeDocumentSummary summary)
    {
        return new KnowledgeDocumentResponse
        {
            DocumentId = summary.DocumentId,
            DisplayName = summary.DisplayName,
            Status = summary.Status,
            FailureReason = summary.FailureReason,
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
