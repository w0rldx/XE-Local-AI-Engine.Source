namespace XE_Local_AI_Engine.Client.Endpoints.Knowledge.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     FastEndpoints handler for a corpus-wide reindex (POST). Resets every document whose embedding model differs from
///     the currently configured one to Pending and enqueues each for re-ingestion, so a model change can rebuild only the
///     stale documents. Returns how many documents were enqueued. Bodyless — uses <c>EndpointWithoutRequest</c> so no
///     JSON body is expected.
/// </summary>
public sealed class ReindexCorpusEndpoint(
    IKnowledgeDocumentCatalogService catalogService,
    IKnowledgeIngestionDispatcher ingestionDispatcher)
    : EndpointWithoutRequest<ReindexCorpusResponse>
{
    private readonly IKnowledgeDocumentCatalogService _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
    private readonly IKnowledgeIngestionDispatcher _ingestionDispatcher = ingestionDispatcher ?? throw new ArgumentNullException(nameof(ingestionDispatcher));

    public override void Configure()
    {
        Post(LocalApiRoutes.KnowledgeBase.Reindex);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var staleIds = await _catalogService.ResetStaleDocumentsToPendingAsync(ct).ConfigureAwait(false);
        foreach (var documentId in staleIds)
        {
            await _ingestionDispatcher.EnqueueAsync(documentId, ct).ConfigureAwait(false);
        }

        await Send.OkAsync(new ReindexCorpusResponse
            {
                EnqueuedCount = staleIds.Count
            },
            ct).ConfigureAwait(false);
    }
}
