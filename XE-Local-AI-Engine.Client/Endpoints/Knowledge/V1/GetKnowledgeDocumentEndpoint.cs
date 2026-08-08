namespace XE_Local_AI_Engine.Client.Endpoints.Knowledge.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     FastEndpoints handler for reading one knowledge-base document's detail plus its ordered chunks (GET by id), for the
///     detail drawer. Returns 404 when the id is unknown.
/// </summary>
public sealed class GetKnowledgeDocumentEndpoint(IKnowledgeDocumentCatalogService catalogService)
    : Endpoint<KnowledgeDocumentRouteRequest, KnowledgeDocumentDetailResponse>
{
    private readonly IKnowledgeDocumentCatalogService _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));

    public override void Configure()
    {
        Get(LocalApiRoutes.KnowledgeBase.DocumentById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(KnowledgeDocumentRouteRequest req, CancellationToken ct)
    {
        var detail = await _catalogService.GetAsync(req.DocumentId, ct).ConfigureAwait(false);
        if (detail is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(ToResponse(detail), ct).ConfigureAwait(false);
    }

    private static KnowledgeDocumentDetailResponse ToResponse(KnowledgeDocumentDetail detail)
    {
        return new KnowledgeDocumentDetailResponse
        {
            DocumentId = detail.DocumentId,
            DisplayName = detail.DisplayName,
            Status = detail.Status,
            FailureReason = detail.FailureReason,
            ChunkCount = detail.ChunkCount,
            EmbeddingModel = detail.EmbeddingModel,
            StaleModel = detail.StaleModel,
            SizeBytes = detail.SizeBytes,
            CreatedAtUtc = detail.CreatedAtUtc,
            UpdatedAtUtc = detail.UpdatedAtUtc,
            Chunks =
            [
                .. detail.Chunks.Select(static chunk => new KnowledgeDocumentChunkResponse
                {
                    ChunkIndex = chunk.ChunkIndex,
                    HeadingPath = chunk.HeadingPath,
                    Content = chunk.Content
                })
            ]
        };
    }
}
