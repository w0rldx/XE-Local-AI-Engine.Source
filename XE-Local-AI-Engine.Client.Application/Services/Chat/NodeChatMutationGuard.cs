namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     Reusable, authoritative server-side guard that rejects mutations targeting an
///     <c>Origin=Remote</c> conversation. Remote-origin rows are node-local mirrors of platform-served chats and
///     are view-only on the node: they must never sync back, and the node retains no epoch key to re-drive them.
///     Applied to ALL content/state mutation entry points (send, rename, pin, archive, branch, revision, feedback,
///     regenerate). UI hiding is cosmetic; this guard is the source of truth. See
///     Plans/schema-contract-sheet.md §4.
/// </summary>
public interface INodeChatMutationGuard
{
    /// <summary>
    ///     Throws <see cref="NodeChatReadOnlyConversationException" /> when the conversation's origin is Remote.
    ///     No-op when the origin is Local OR the conversation does not exist (the caller's own NotFound handling
    ///     stays authoritative — the guard never masks a missing conversation).
    /// </summary>
    Task EnsureMutableAsync(Guid conversationId, CancellationToken cancellationToken = default);
}

/// <summary>
///     Represents node chat mutation guard.
/// </summary>
public sealed class NodeChatMutationGuard(INodeChatPersistenceService persistence) : INodeChatMutationGuard
{
    private readonly INodeChatPersistenceService _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));

    public async Task EnsureMutableAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        // Read ONLY the origin column. The guard never touches INodeKeyRegistry / epoch keys.
        var origin = await _persistence.GetConversationOriginAsync(conversationId, cancellationToken).ConfigureAwait(false);

        if (string.Equals(origin, NodeChatOriginValues.Remote, StringComparison.Ordinal))
        {
            throw new NodeChatReadOnlyConversationException(conversationId);
        }
    }
}

/// <summary>
///     Thrown when a mutation targets a read-only (<c>Origin=Remote</c>) conversation. Mutation endpoints map this
///     to HTTP 409 Conflict; the local send/stream path lets it propagate to the caller.
/// </summary>
public sealed class NodeChatReadOnlyConversationException(Guid conversationId)
    : InvalidOperationException($"Conversation {conversationId} is read-only because it has remote origin.")
{
    public const string Code = "conversation-read-only";
    public const string Reason = "remote-origin";

    public Guid ConversationId { get; } = conversationId;
}
