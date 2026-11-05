namespace XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     The authoritative server-side guard rejecting mutations that target an <c>Origin=Remote</c> conversation.
/// </summary>
/// <remarks>
///     Remote-origin rows are view-only mirrors of platform-served chats; UI hiding is cosmetic beside this guard. It
///     reads only the plaintext <c>origin</c> column and runs inside the Application services, never an endpoint: the
///     operator mutations of <c>NodeChatPersistenceService</c>, compaction, attachment upload and delete, and every
///     turn start. Approval and <c>ask_user</c> resolutions key on a request id and stay unguarded, since no Remote
///     conversation can hold a parked turn.
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
