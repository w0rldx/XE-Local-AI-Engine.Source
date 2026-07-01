namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     No-op <see cref="IKnowledgeIndexingNotifier" />. Registered as the default in <c>AddNodeKnowledgeBase</c> so the
///     ingestion service resolves a notifier even when no SignalR hub is wired (Application-only and test hosts). The
///     Client host registers a hub-backed notifier that supersedes this one.
/// </summary>
internal sealed class NullKnowledgeIndexingNotifier : IKnowledgeIndexingNotifier
{
    public Task NotifyDocumentChangedAsync(Guid documentId, KnowledgeDocumentStatus status, CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
