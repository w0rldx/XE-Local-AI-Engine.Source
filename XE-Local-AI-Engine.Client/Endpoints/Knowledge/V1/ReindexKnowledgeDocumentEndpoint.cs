namespace XE_Local_AI_Engine.Client.Endpoints.Knowledge.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     FastEndpoints handler for reindexing one knowledge-base document (POST by id). Resets the document to Pending and
///     re-enqueues it; the background ingestion worker idempotently purges the document's old chunks/vectors before
///     re-inserting, so a reindex never duplicates rows. Returns 404 when the id is unknown, otherwise 204.
/// </summary>
public sealed class ReindexKnowledgeDocumentEndpoint(
    IKnowledgeDocumentCatalogService catalogService,
    IKnowledgeIngestionDispatcher ingestionDispatcher)
    : Endpoint<KnowledgeDocumentRouteRequest>
{
    private readonly IKnowledgeDocumentCatalogService _catalogService = catalogService ?? throw new ArgumentNullException(nameof(catalogService));
    private readonly IKnowledgeIngestionDispatcher _ingestionDispatcher = ingestionDispatcher ?? throw new ArgumentNullException(nameof(ingestionDispatcher));

    public override void Configure()
    {
        Post(LocalApiRoutes.KnowledgeBase.DocumentReindex);
        // The action carries no body; declare the route request so a bodyless POST is not rejected with a 415.
        Description(builder => builder.Accepts<KnowledgeDocumentRouteRequest>());
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(KnowledgeDocumentRouteRequest req, CancellationToken ct)
    {
        var reset = await _catalogService.ResetToPendingAsync(req.DocumentId, ct).ConfigureAwait(false);
        if (!reset)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        var admission = await _ingestionDispatcher.EnqueueAsync(req.DocumentId, ct).ConfigureAwait(false);
        if (admission == KnowledgeIngestionEnqueueResult.QueueFull)
        {
            // The document is reset to Pending but the bounded ingestion queue is full, so it was not admitted now; the
            // background worker recovers Pending documents on a later start, and a retry once the queue drains re-enqueues
            // it. Signal a retryable busy state rather than reporting success for work that was not queued.
            HttpContext.Response.Headers.RetryAfter = "5";
            await Send.StringAsync("The server is busy indexing documents. Please retry shortly.",
                StatusCodes.Status503ServiceUnavailable,
                cancellation: ct).ConfigureAwait(false);
            return;
        }

        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}
