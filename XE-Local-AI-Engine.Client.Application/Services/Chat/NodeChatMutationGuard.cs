namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     The authoritative server-side guard rejecting mutations that target an <c>Origin=Remote</c> conversation.
/// </summary>
/// <remarks>
///     Remote-origin rows are node-local mirrors of platform-served chats and are view-only: they must never sync
///     back, and the node retains no epoch key to re-drive them. It applies to ALL content and state mutation entry
///     points, and UI hiding is cosmetic beside it. It reads only the plaintext <c>origin</c> column, never touching
///     the epoch key registry.
/// </remarks>
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
public sealed class NodeChatMutationGuard : INodeChatMutationGuard
{
    private readonly INodeChatPersistenceService _persistence;

    public NodeChatMutationGuard(INodeChatPersistenceService persistence)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        _persistence = persistence;
    }

    public async Task EnsureMutableAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        // Read ONLY the origin column. The guard never touches INodeKeyRegistry / epoch keys.
        var origin = await _persistence.GetConversationOriginAsync(conversationId, cancellationToken);

        if (string.Equals(origin, NodeChatOriginValues.Remote, StringComparison.Ordinal))
        {
            throw new NodeChatReadOnlyConversationException(conversationId);
        }
    }
}

/// <summary>
///     Thrown when a mutation targets a read-only (<c>Origin=Remote</c>) conversation.
/// </summary>
/// <remarks>
///     On the REST path the global <c>ConflictExceptionHandler</c> turns it into a 409 with
///     <c>conflictType = ReadOnlyConversation</c>, so endpoints must let it propagate and never catch it; the local
///     send and stream path propagates it to the caller.
/// </remarks>
public sealed class NodeChatReadOnlyConversationException : InvalidOperationException
{
    public NodeChatReadOnlyConversationException(Guid conversationId) : base($"Conversation {conversationId} is read-only because it has remote origin.")
    {
        ConversationId = conversationId;
    }

    public Guid ConversationId { get; }
}
