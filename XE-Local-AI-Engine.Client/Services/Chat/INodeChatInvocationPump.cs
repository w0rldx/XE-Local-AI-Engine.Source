namespace XE_Local_AI_Engine.Client.Services.Chat;

using XE_Local_AI_Engine.Client.Models.Enums;
using XE_Local_AI_Engine.Client.Services.Events;

/// <summary>
/// Shared per-invocation persistence pump (Phase 0.2). Persists streamed deltas and terminal states for one
/// agent run through <see cref="INodeChatPersistenceService"/>, invoked by BOTH the local loopback front door
/// and the platform path. See <see cref="NodeChatInvocationPump"/> for the contract details.
/// </summary>
public interface INodeChatInvocationPump
{
    Task<NodeChatPumpFlushResult> FlushDeltaAsync(NodeChatMessageCorrelation correlation,
        InvocationState state,
        NodeChatPumpCursor cursor,
        CancellationToken cancellationToken = default);

    Task<NodeChatPumpTerminalResult> TerminalizeAsync(NodeChatMessageCorrelation correlation,
        InvocationState state,
        string? requestedModel);

    Task<NodeChatPumpTerminalResult> TerminalizeInterruptedAsync(NodeChatMessageCorrelation correlation,
        NodeChatPumpCursor cursor,
        bool wasCancelled);
}
