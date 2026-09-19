namespace XE_Local_AI_Engine.Client.Services.Invocation.Implementation;

using System.Collections.Concurrent;

/// <summary>
///     Owns the lifetime of registered tool calls: the per-invocation tool-result budget and the stale sweep that
///     releases a call nothing will ever answer. Shares the one <see cref="PendingToolCallRegistry" /> with
///     <see cref="ToolApprovalCoordinator" /> and <see cref="InvocationRunner" />, so a call registered by the
///     approval path is visible to the approval resolve, the runner's cancel/drain path, and the sweep alike.
///     <para>
///         A singleton for the same reason the coordinator is: the sweep runs from a background service, on a
///         different call stack than the turn whose calls it releases.
///     </para>
///     <para>
///         This used to also own an API-side (platform) tool round-trip, shipping the request over the outbound
///         worker hub and waiting for the result event that released it. Nothing in this repo ever offers a tool at
///         that location — every tool offer a node builds is <c>ToolLocation.ClientLocal</c> — and the hub it
///         answered on is gone, so only the registry lifetime remains.
///     </para>
/// </summary>
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
