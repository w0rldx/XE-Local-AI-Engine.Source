namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Models;

/// <summary>
///     Default <see cref="INodeChatRemotePersistenceCoordinator" />. Persistence-only sibling of the local front
///     door: it ensures the conversation, persists the synthesized user turn + assistant placeholder with
///     Origin=Remote, and hands back a session that drives the shared <see cref="INodeChatInvocationPump" />.
/// </summary>
public sealed class NodeChatRemotePersistenceCoordinator(
    INodeChatPersistenceService persistence,
    INodeChatInvocationPump invocationPump,
    TimeProvider timeProvider) : INodeChatRemotePersistenceCoordinator
{
    private const int RemoteTitleMaxLength = 120;
    private readonly INodeChatInvocationPump _invocationPump = invocationPump ?? throw new ArgumentNullException(nameof(invocationPump));

    private readonly INodeChatPersistenceService _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<NodeChatRemotePersistenceSession?> BeginAsync(RuntimePackage package,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        var nowUtc = NowUnixMilliseconds();

        // Ensure the node-local conversation row exists for this platform conversation. Idempotent: an existing
        // row (any state) is returned as-is so a user-renamed/locally-created conv is never clobbered.
        await _persistence.EnsureConversationAsync(new NodeChatEnsureConversationRequest(package.ConversationId,
                ResolveTitle(package),
                package.ClientNodeId.ToString(),
                nowUtc,
                NodeChatOriginValues.Remote),
            cancellationToken).ConfigureAwait(false);

        // Node mints a FRESH assistant message id; RequestId == InvocationId so the run's state stream correlates.
        // The user turn is synthesized from the last ConversationContext entry (the just-sent user message). The
        // platform's package.MessageId is NOT reused as a node message id.
        var assistantMessageId = Guid.NewGuid();
        var correlation = new NodeChatMessageCorrelation(package.ConversationId, assistantMessageId, package.InvocationId);

        var userTurn = ResolveUserTurn(package);
        if (userTurn is not null)
        {
            await _persistence.PersistUserMessageAsync(new NodeChatPersistUserMessageRequest(package.ConversationId,
                    Guid.NewGuid(),
                    userTurn,
                    nowUtc,
                    Origin: NodeChatOriginValues.Remote),
                cancellationToken).ConfigureAwait(false);
        }

        // No AgentDefinitionId is stamped here (unlike the local NodeChatStreamService send path): a
        // platform/worker-dispatched RuntimePackage carries only AgentDefinitionVersion + ResolvedSystemPrompt, never
        // the agent definition id — that id lives in the cross-repo server envelope contract, not on this payload — so
        // there is nothing to attribute by without a server/envelope contract change. Feedback on these remote-origin
        // turns therefore aggregates as unbound, by design. (User-initiated Codex/cloud-model chat sends still attribute
        // correctly: those run through NodeChatStreamService, which resolves and stamps the effective agent id.)
        await _persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(package.ConversationId,
                assistantMessageId,
                package.InvocationId,
                nowUtc,
                package.ModelProfile,
                Origin: NodeChatOriginValues.Remote),
            cancellationToken).ConfigureAwait(false);

        // The streaming mark is guarded (StreamingSources): it is an atomic no-op if the row already left the
        // Pending/Queued set — e.g. a cancel terminalized the placeholder before we got here. In that case the returned
        // row carries the true terminal status, NOT Streaming; opening a persistence session then would drive the pump
        // against an already-terminal row (every later flush/terminalize is guard-rejected, so the work is pointless).
        // Abort honestly by returning null so the dispatcher runs the invocation without a node-local mirror.
        var streaming = await _persistence.MarkAssistantStreamingAsync(correlation, NowUnixMilliseconds(), cancellationToken).ConfigureAwait(false);
        if (!string.Equals(streaming.Status, NodeChatMessageStatusValues.Streaming, StringComparison.Ordinal))
        {
            return null;
        }

        return new NodeChatRemotePersistenceSession(_invocationPump, correlation, package.ModelProfile);
    }

    private static string? ResolveTitle(RuntimePackage package)
    {
        var firstUser = package.ConversationContext
                               .Where(static message => message.Role == MessageRole.User && !string.IsNullOrWhiteSpace(message.Content))
                               .Select(static message => message.Content.Trim())
                               .FirstOrDefault();

        if (string.IsNullOrEmpty(firstUser))
        {
            return null;
        }

        return firstUser.Length <= RemoteTitleMaxLength ? firstUser : firstUser[..RemoteTitleMaxLength];
    }

    private static string? ResolveUserTurn(RuntimePackage package)
    {
        var lastUser = package.ConversationContext
                              .Where(static message => message.Role == MessageRole.User && !string.IsNullOrWhiteSpace(message.Content))
                              .Select(static message => message.Content.Trim())
                              .LastOrDefault();

        return string.IsNullOrEmpty(lastUser) ? null : lastUser;
    }

    private long NowUnixMilliseconds()
    {
        return _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
    }
}
