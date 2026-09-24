namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Hub-backed <see cref="IKnowledgeIndexingNotifier" />, superseding the no-op default.
/// </summary>
/// <remarks>
///     Broadcasts each document status change to all connected clients under
///     <see cref="KnowledgeBaseHubEvents.DocumentChanged" /> as the SignalR method name, so the React client subscribes
///     by event name and invalidates the documents list. The payload is already sanitized (id + coarse status only).
///     Publishing is best-effort: a transport failure is swallowed and logged by type, never propagated, so a hub
///     hiccup can never fail or stall the background ingestion pipeline.
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

    public async Task NotifyDocumentChangedAsync(Guid documentId, KnowledgeDocumentStatus status, CancellationToken cancellationToken = default)
    {
        var payload = new KnowledgeDocumentChangedHubEvent
        {
            EventType = KnowledgeBaseHubEvents.DocumentChanged,
            DocumentId = documentId,
            Status = status,
            OccurredAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };

        try
        {
            await _hubContext.Clients.All.SendAsync(KnowledgeBaseHubEvents.DocumentChanged, payload, cancellationToken);
        }
        catch (Exception exception)
        {
            // Best-effort notification only — the documents-list refetch is the source of truth. A push failure must never
            // fail ingestion, so swallow it and log the exception type only (never document content).
            _logger.LogWarning("Could not publish a knowledge-base indexing event for document {DocumentId} ({ErrorClass}).", documentId, exception.GetType().Name);
        }
    }
}
