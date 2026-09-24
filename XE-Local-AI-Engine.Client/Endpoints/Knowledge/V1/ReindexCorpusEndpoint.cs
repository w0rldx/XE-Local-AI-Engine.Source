namespace XE_Local_AI_Engine.Client.Endpoints.Knowledge.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     A corpus-wide reindex, returning how many documents were enqueued. Body-less, so no JSON body is expected.
/// </summary>
/// <remarks>
///     Resets every document whose embedding model differs from the currently configured one to Pending and enqueues
///     each for re-ingestion, so a model change rebuilds only the stale documents.
/// </remarks>
public sealed class ReindexCorpusEndpoint : EndpointWithoutRequest<ReindexCorpusResponse>
{
    private readonly IKnowledgeDocumentCatalogService _catalogService;
    private readonly IKnowledgeIngestionDispatcher _ingestionDispatcher;

    public ReindexCorpusEndpoint(IKnowledgeDocumentCatalogService catalogService,
        IKnowledgeIngestionDispatcher ingestionDispatcher)
    {
        ArgumentNullException.ThrowIfNull(catalogService);
        ArgumentNullException.ThrowIfNull(ingestionDispatcher);
        _catalogService = catalogService;
        _ingestionDispatcher = ingestionDispatcher;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.KnowledgeBase.Reindex);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var staleIds = await _catalogService.ResetStaleDocumentsToPendingAsync(ct);
        var enqueued = 0;
        foreach (var documentId in staleIds)
        {
            var admission = await _ingestionDispatcher.EnqueueAsync(documentId, ct);
            if (admission == KnowledgeIngestionEnqueueResult.QueueFull)
            {
                // The bounded queue filled part-way through a corpus reindex. Documents already reset to Pending but not admitted are recovered by the worker on a later
                // start, or by a retry once the queue drains; report the count actually enqueued, so the caller sees an honest number rather than the full stale total.
                break;
            }

            enqueued++;
        }

        await Send.OkAsync(new ReindexCorpusResponse
            {
                EnqueuedCount = enqueued
            },
            ct);
    }
}
