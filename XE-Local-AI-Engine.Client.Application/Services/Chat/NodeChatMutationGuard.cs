namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Persistence.Entities;

/// <summary>
///     The authoritative server-side guard rejecting mutations of a read-only conversation: an <c>Origin=Remote</c>
///     mirror, or a <c>scheduled</c> run's stored answer (ADR 0024).
/// </summary>
/// <remarks>
///     UI hiding is cosmetic beside this guard. It reads only the plaintext <c>origin</c> and <c>kind</c> columns and runs
///     inside the Application services, never an endpoint: the operator mutations of <c>NodeChatPersistenceService</c>,
///     compaction, attachment upload and delete, and every turn start. Approval and <c>ask_user</c> resolutions key on a
///     request id and stay unguarded, since neither kind can hold a parked turn. Deleting a scheduled answer stays allowed.
/// </remarks>
public interface INodeChatMutationGuard
{
    /// <summary>
    ///     Throws <see cref="NodeChatReadOnlyConversationException" /> when the conversation's origin is Remote or its kind
    ///     is <c>scheduled</c>. No-op for a missing conversation: the caller's own NotFound handling stays authoritative.
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
        await EnsureDeletableAsync(conversationId, cancellationToken);

        if (string.Equals(await _persistence.GetConversationKindAsync(conversationId, cancellationToken), NodeConversationKind.Scheduled, StringComparison.Ordinal))
        {
            throw new NodeChatReadOnlyConversationException(conversationId, "it is a scheduled run's answer");
        }
    }

    /// <summary>The delete path's guard: only a Remote mirror is refused, so a scheduled answer can still be removed.</summary>
    public async Task EnsureDeletableAsync(Guid conversationId, CancellationToken cancellationToken = default)
    {
        // Read ONLY the plaintext columns. The guard never touches INodeKeyRegistry / epoch keys.
        var origin = await _persistence.GetConversationOriginAsync(conversationId, cancellationToken);

        if (string.Equals(origin, NodeChatOriginValues.Remote, StringComparison.Ordinal))
        {
            throw new NodeChatReadOnlyConversationException(conversationId);
        }
    }
}
