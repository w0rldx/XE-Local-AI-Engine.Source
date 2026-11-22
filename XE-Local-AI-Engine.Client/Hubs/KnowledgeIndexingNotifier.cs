namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Hub-backed <see cref="IKnowledgeIndexingNotifier" />, superseding the no-op default.
/// </summary>
/// <remarks>
///     Broadcasts each document status change to all connected clients under
///     <see cref="KnowledgeBaseHubEvents.DocumentChanged" /> as the SignalR method name (a deletion and a status-free
///     refresh under their own names), so the React client subscribes by event name and invalidates the documents list.
///     Payloads carry an id and coarse status at most. Publishing is best-effort: a transport failure is swallowed and
///     logged by type, never propagated, so a hub hiccup can never fail or stall the background ingestion pipeline.
/// </remarks>
internal sealed class KnowledgeIndexingNotifier : IKnowledgeIndexingNotifier
{
    private readonly IHubContext<KnowledgeBaseHub> _hubContext;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<KnowledgeIndexingNotifier> _logger;

    public KnowledgeIndexingNotifier(IHubContext<KnowledgeBaseHub> hubContext,
        TimeProvider timeProvider,
        ILogger<KnowledgeIndexingNotifier> logger)
    {
        ArgumentNullException.ThrowIfNull(hubContext);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _hubContext = hubContext;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Task NotifyDocumentChangedAsync(Guid documentId, KnowledgeDocumentStatus status, CancellationToken cancellationToken = default)
    {
        return BroadcastAsync(KnowledgeBaseHubEvents.DocumentChanged,
            documentId,
            new KnowledgeDocumentChangedHubEvent
            {
                EventType = KnowledgeBaseHubEvents.DocumentChanged,
                DocumentId = documentId,
                Status = status,
                OccurredAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
            },
            cancellationToken);
    }

    public Task NotifyDocumentRemovedAsync(Guid documentId, CancellationToken cancellationToken = default)
    {
        return BroadcastAsync(KnowledgeBaseHubEvents.DocumentRemoved,
            documentId,
            new KnowledgeDocumentRemovedHubEvent
            {
                EventType = KnowledgeBaseHubEvents.DocumentRemoved,
                DocumentId = documentId,
                OccurredAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
            },
            cancellationToken);
    }

    public Task NotifyDocumentsChangedAsync(CancellationToken cancellationToken = default)
    {
        return BroadcastAsync(KnowledgeBaseHubEvents.DocumentsChanged,
            documentId: null,
            new KnowledgeDocumentsChangedHubEvent
            {
                EventType = KnowledgeBaseHubEvents.DocumentsChanged,
                OccurredAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
            },
            cancellationToken);
    }

    private async Task BroadcastAsync(string method, Guid? documentId, object payload, CancellationToken cancellationToken)
    {
        try
        {
            await _hubContext.Clients.All.SendAsync(method, payload, cancellationToken);
        }
        catch (Exception exception)
        {
            // Best-effort notification only — the documents-list refetch is the source of truth. A push failure must never
            // fail ingestion, so swallow it and log the exception type only (never document content).
            _logger.LogWarning("Could not publish the knowledge-base event {HubEvent} for document {DocumentId} ({ErrorClass}).",
                method,
                documentId,
                exception.GetType().Name);
        }
    }
}
