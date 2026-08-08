namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
///     Drives the shared pump for one remote invocation. The caller feeds it each <see cref="InvocationState" /> the
///     run produces; deltas are flushed and the terminal/interrupted state is persisted. No SSE events are emitted —
///     the platform path needs persistence only.
/// </summary>
public sealed class NodeChatRemotePersistenceSession(
    INodeChatInvocationPump invocationPump,
    NodeChatMessageCorrelation correlation,
    string? requestedModel)
{
    private readonly NodeChatMessageCorrelation _correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));
    private readonly INodeChatInvocationPump _invocationPump = invocationPump ?? throw new ArgumentNullException(nameof(invocationPump));
    private NodeChatPumpCursor _cursor = NodeChatPumpCursor.Empty;
    private bool _terminalPersisted;

    /// <summary>
    ///     Persists a streamed delta and, when the state is terminal, terminalizes the assistant message. Returns
    ///     true once a terminal state has been persisted so the caller can stop feeding states.
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
    ///     Terminalizes a remote stream that ended WITHOUT a terminal state (process/stream loss or cancellation),
    ///     writing the last-seen content. No-op if a terminal state was already persisted.
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
