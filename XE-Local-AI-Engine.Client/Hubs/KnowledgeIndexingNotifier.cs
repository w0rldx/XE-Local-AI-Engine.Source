namespace XE_Local_AI_Engine.Client.Hubs;

using Microsoft.AspNetCore.SignalR;
using XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Hub-backed <see cref="IKnowledgeIndexingNotifier" />. Broadcasts each document status change to all connected
///     clients under <see cref="KnowledgeBaseHubEvents.DocumentChanged" /> as the SignalR method name, so the React client
///     subscribes by event name and invalidates the documents list. Supersedes the no-op default. The payload is already
///     sanitized (id + coarse status only). Publishing is best-effort: a transport failure is swallowed and logged by
///     type, never propagated, so a hub hiccup can never fail or stall the background ingestion pipeline.
/// </summary>
internal sealed class KnowledgeIndexingNotifier(
    IHubContext<KnowledgeBaseHub> hubContext,
    TimeProvider timeProvider,
    ILogger<KnowledgeIndexingNotifier> logger) : IKnowledgeIndexingNotifier
{
    private readonly IHubContext<KnowledgeBaseHub> _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<KnowledgeIndexingNotifier> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task NotifyDocumentChangedAsync(Guid documentId, KnowledgeDocumentStatus status, CancellationToken cancellationToken = default)
    {
        var payload = new KnowledgeDocumentChangedHubEvent(
            KnowledgeBaseHubEvents.DocumentChanged,
            documentId,
            status,
            _timeProvider.GetUtcNow().ToUnixTimeMilliseconds());

        try
        {
            await _hubContext.Clients.All.SendAsync(KnowledgeBaseHubEvents.DocumentChanged, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Best-effort notification only — the documents-list refetch is the source of truth. A push failure must never
            // fail ingestion, so swallow it and log the exception type only (never document content).
            _logger.LogWarning("Could not publish a knowledge-base indexing event for document {DocumentId} ({ErrorClass}).", documentId, exception.GetType().Name);
        }
    }
}
