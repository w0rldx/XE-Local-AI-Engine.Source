namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Models;
using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
/// Default <see cref="INodeChatRemotePersistenceCoordinator"/>. Persistence-only sibling of the local front
/// door: it ensures the conversation, persists the synthesized user turn + assistant placeholder with
/// Origin=Remote, and hands back a session that drives the shared <see cref="INodeChatInvocationPump"/>.
/// </summary>
public sealed class NodeChatRemotePersistenceCoordinator(
    INodeChatPersistenceService persistence,
    INodeChatInvocationPump invocationPump,
    TimeProvider timeProvider) : INodeChatRemotePersistenceCoordinator
{
    private const int RemoteTitleMaxLength = 120;

    private readonly INodeChatPersistenceService _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
    private readonly INodeChatInvocationPump _invocationPump = invocationPump ?? throw new ArgumentNullException(nameof(invocationPump));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<NodeChatRemotePersistenceSession> BeginAsync(RuntimePackage package,
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

        await _persistence.CreateAssistantPlaceholderAsync(new NodeChatCreateAssistantPlaceholderRequest(package.ConversationId,
                assistantMessageId,
                package.InvocationId,
                nowUtc,
                package.ModelProfile,
                Origin: NodeChatOriginValues.Remote),
            cancellationToken).ConfigureAwait(false);

        await _persistence.MarkAssistantStreamingAsync(correlation, NowUnixMilliseconds(), cancellationToken).ConfigureAwait(false);

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

/// <summary>
/// Drives the shared pump for one remote invocation. The caller feeds it each <see cref="InvocationState"/> the
/// run produces; deltas are flushed and the terminal/interrupted state is persisted. No SSE events are emitted —
/// the platform path needs persistence only.
/// </summary>
public sealed class NodeChatRemotePersistenceSession(
    INodeChatInvocationPump invocationPump,
    NodeChatMessageCorrelation correlation,
    string? requestedModel)
{
    private readonly INodeChatInvocationPump _invocationPump = invocationPump ?? throw new ArgumentNullException(nameof(invocationPump));
    private readonly NodeChatMessageCorrelation _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
    private NodeChatPumpCursor _cursor = NodeChatPumpCursor.Empty;
    private bool _terminalPersisted;

    /// <summary>
    /// Persists a streamed delta and, when the state is terminal, terminalizes the assistant message. Returns
    /// true once a terminal state has been persisted so the caller can stop feeding states.
    /// </summary>
    public async Task<bool> ApplyAsync(InvocationState state, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (_terminalPersisted)
        {
            return true;
        }

        var flush = await _invocationPump.FlushDeltaAsync(_correlation, state, _cursor, cancellationToken).ConfigureAwait(false);
        _cursor = flush.Cursor;

        if (NodeChatInvocationPump.IsTerminal(state.Status))
        {
            await _invocationPump.TerminalizeAsync(_correlation, state, requestedModel).ConfigureAwait(false);
            _terminalPersisted = true;
        }

        return _terminalPersisted;
    }

    /// <summary>
    /// Terminalizes a remote stream that ended WITHOUT a terminal state (process/stream loss or cancellation),
    /// writing the last-seen content. No-op if a terminal state was already persisted.
    /// </summary>
    public async Task TerminalizeInterruptedAsync(bool wasCancelled)
    {
        if (_terminalPersisted)
        {
            return;
        }

        await _invocationPump.TerminalizeInterruptedAsync(_correlation, _cursor, wasCancelled).ConfigureAwait(false);
        _terminalPersisted = true;
    }
}
