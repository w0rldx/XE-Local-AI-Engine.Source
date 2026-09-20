namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Collections.Concurrent;

/// <summary>
///     Owns the lifetime of registered tool calls: the per-invocation tool-result budget, and the stale sweep that
///     releases a call nothing will ever answer.
/// </summary>
/// <remarks>
///     Shares the one <see cref="PendingToolCallRegistry" /> with <see cref="ToolApprovalCoordinator" /> and
///     <see cref="InvocationRunner" />, so a call registered by the approval path is visible to the approval resolve,
///     the runner's cancel/drain path and the sweep alike. A singleton for the coordinator's reason: the sweep runs
///     from a background service, on a different call stack than the turn whose calls it releases. Every tool offer a
///     node builds is <c>ToolLocation.ClientLocal</c>, so only the registry lifetime lives here.
/// </remarks>
public sealed class ApiToolCallBridge
{
    // The SAME dictionary instance the runner and ToolApprovalCoordinator hold (see PendingToolCallRegistry).
    private readonly ConcurrentDictionary<string, PendingToolCall> _pendingToolCalls;
    private readonly TimeProvider _timeProvider;

    // The effective tool-result wait budget for each active invocation, seeded from the package's
    // ToolCallTimeoutSeconds when RunAsync starts and cleared again when the turn ends.
    private readonly ConcurrentDictionary<Guid, TimeSpan> _toolResultTimeoutsByInvocation = new();

    public ApiToolCallBridge(PendingToolCallRegistry pendingToolCallRegistry, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(pendingToolCallRegistry);
        _pendingToolCalls = pendingToolCallRegistry.Calls;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public void CleanupStaleToolCalls(TimeSpan maxAge)
    {
        var cutoff = _timeProvider.GetUtcNow() - maxAge;

        foreach (var pendingToolCall in _pendingToolCalls)
        {
            if (pendingToolCall.Value.CreatedAt >= cutoff)
            {
                continue;
            }

            if (_pendingToolCalls.TryRemove(pendingToolCall.Key, out var removedPendingToolCall))
            {
                var timeoutException = new TimeoutException("Tool call timed out during cleanup.");
                removedPendingToolCall.ApprovalCompletion.TrySetException(timeoutException);
            }
        }
    }

    // Seeded by InvocationRunner.RunAsync from the package's resolved TurnPolicy, and cleared again when the turn ends.
    public void SetToolResultTimeout(Guid invocationId, TimeSpan toolResultTimeout)
    {
        _toolResultTimeoutsByInvocation[invocationId] = toolResultTimeout;
    }

    public void ClearToolResultTimeout(Guid invocationId)
    {
        _toolResultTimeoutsByInvocation.TryRemove(invocationId, out _);
    }
}
