namespace XE_Local_AI_Engine.Client.Endpoints.Knowledge.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Reindexes one knowledge-base document: 404 when the id is unknown, otherwise 204.
/// </summary>
/// <remarks>
///     Resets the document to Pending and re-enqueues it; the background ingestion worker idempotently purges the
///     document's old chunks and vectors before re-inserting, so a reindex never duplicates rows.
/// </remarks>
public sealed class ReindexKnowledgeDocumentEndpoint : Endpoint<KnowledgeDocumentRouteRequest>
{
    private readonly IKnowledgeDocumentCatalogService _catalogService;
    private readonly IKnowledgeIngestionDispatcher _ingestionDispatcher;

    public ReindexKnowledgeDocumentEndpoint(
        IKnowledgeDocumentCatalogService catalogService,
        IKnowledgeIngestionDispatcher ingestionDispatcher)
    {
        ArgumentNullException.ThrowIfNull(catalogService);
        ArgumentNullException.ThrowIfNull(ingestionDispatcher);
        _catalogService = catalogService;
        _ingestionDispatcher = ingestionDispatcher;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.KnowledgeBase.DocumentReindex);
        // The action carries no body; declare the route request so a bodyless POST is not rejected with a 415.
        Description(builder => builder.Accepts<KnowledgeDocumentRouteRequest>());
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(KnowledgeDocumentRouteRequest req, CancellationToken ct)
    {
        var reset = await _catalogService.ResetToPendingAsync(req.DocumentId, ct);
        if (!reset)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        var admission = await _ingestionDispatcher.EnqueueAsync(req.DocumentId, ct);
        if (admission == KnowledgeIngestionEnqueueResult.QueueFull)
        {
            // The document is reset to Pending but the bounded ingestion queue is full, so it was not admitted now; the background worker recovers Pending documents on a
            // later start, and a retry once the queue drains re-enqueues it. Signal a retryable busy state rather than reporting success for work that was not queued.
            HttpContext.Response.Headers.RetryAfter = "5";
            await Send.StringAsync("The server is busy indexing documents. Please retry shortly.",
                StatusCodes.Status503ServiceUnavailable,
                cancellation: ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
