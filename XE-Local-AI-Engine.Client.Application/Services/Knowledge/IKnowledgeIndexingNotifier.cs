namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Application-side seam the ingestion service calls on every document lifecycle transition, so a connected
///     operator is pushed a status change and the React documents list invalidates and refetches.
/// </summary>
/// <remarks>
///     Defined in Application so the scoped ingestion service can depend on it without referencing the Client project or
///     SignalR; the Client host supplies a hub-backed implementation over <c>IHubContext&lt;KnowledgeBaseHub&gt;</c>.
///     The default registered in <c>AddNodeKnowledgeBase</c> is a no-op, so Application-only and test hosts resolve a
///     notifier with no hub wired.
/// </remarks>
public interface IKnowledgeIndexingNotifier
{
    /// <summary>
    ///     Publishes that one document reached a new <see cref="KnowledgeDocumentStatus" />. Best-effort: the
    ///     implementation must never throw or stall ingestion when the transport fails. The payload carries only the id
    ///     and coarse status — never any document or chunk content.
    /// </summary>
    Task NotifyDocumentChangedAsync(Guid documentId, KnowledgeDocumentStatus status, CancellationToken cancellationToken = default);
}
