namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Application-side seam the ingestion service calls on every document lifecycle transition so a connected operator
///     is pushed a status change (the React documents list then invalidates and refetches). Defined in Application so the
///     scoped ingestion service can depend on it without referencing the Client project or SignalR; the Client host
///     supplies a hub-backed implementation over <c>IHubContext&lt;KnowledgeBaseHub&gt;</c>. The default registered in
///     <c>AddNodeKnowledgeBase</c> is a no-op, so Application-only and test hosts resolve a notifier with no hub wired.
/// </summary>
public interface IKnowledgeIndexingNotifier
{
    /// <summary>
    ///     Publishes that one document reached a new <see cref="KnowledgeDocumentStatus" />. Best-effort: the
    ///     implementation must never throw or stall ingestion when the transport fails. The payload carries only the id
    ///     and coarse status — never any document or chunk content.
    /// </summary>
    Task NotifyDocumentChangedAsync(Guid documentId, KnowledgeDocumentStatus status, CancellationToken cancellationToken = default);
}

/// <summary>
///     Stable SignalR client-method name for knowledge-base indexing events. Doubles as the wire event-type discriminator
///     on the payload so the React client subscribes by name (mirrors <c>SchedulerHubEvents</c>).
/// </summary>
public static class KnowledgeBaseHubEvents
{
    public const string DocumentChanged = "knowledge.documentChanged";
}

/// <summary>
///     Sanitized indexing-status payload. Carries only the document id, its coarse pipeline status, and the instant the
///     transition was observed — deliberately no file name, chunk text, or failure detail (the list refetch is the source
///     of truth for the display name and reason).
/// </summary>
public sealed record KnowledgeDocumentChangedHubEvent(
    string EventType,
    Guid DocumentId,
    KnowledgeDocumentStatus Status,
    long OccurredAtUtc);
