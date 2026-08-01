namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Diagnostics;

/// <summary>
///     Terminalizes a just-created assistant row if the turn is torn down before run ownership is established. Armed
///     immediately after the placeholder/variant is persisted and disarmed by <see cref="OwnershipEstablished" /> once
///     the pump + runner (and their protective finally) exist. Disposal runs on any exit — normal fall-through, an
///     exception, or async-iterator disposal on client disconnect — so a pre-ownership disconnect never strands the row
///     Pending/Queued until the restart reaper. Shared by BOTH local front doors (send and regenerate) so the
///     pre-ownership teardown behaves identically on each.
/// </summary>
internal sealed class PreOwnershipTerminalizationGuard(
    INodeChatPersistenceService persistence,
    NodeChatMessageCorrelation correlation,
    TimeProvider timeProvider,
    ILogger logger) : IAsyncDisposable
{
    // Terminal error stamped when a turn is torn down (client disconnect/cancel) before run ownership was established.
    // Mirrors the Interrupted terminal the restart recovery service assigns to rows orphaned by a crash.
    private const string PreOwnershipInterruptedError = "Interrupted before the response started.";

    private bool _ownershipEstablished;

    public void OwnershipEstablished()
    {
        _ownershipEstablished = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownershipEstablished)
        {
            return;
        }

        // Best-effort, on a fresh token: the client token that triggered this teardown is already cancelled, and the
        // row must still be terminalized. A missing/mismatched row (placeholder already terminalized elsewhere) just
        // logs — this path must never throw out of an iterator disposal.
        try
        {
            // Carry a thin run envelope so this terminal row gets its durable envelope in the SAME transaction as the
            // message row, like the pump's interrupted path — otherwise this pre-ownership teardown is
            // the one live path that writes a terminal without an atomic envelope, self-healing only at the next
            // restart's reconcile. There is no InvocationState here, so invocation id / tokens / duration / model are
            // unknown and omitted; the terminal status (derived from the winning row) carries the interrupted outcome.
            await persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest(correlation,
                    NodeChatMessageStatusValues.Interrupted,
                    timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                    Error: PreOwnershipInterruptedError,
                    Envelope: new AgentRunEnvelopeMetadata(InvocationId: null, DurationMs: 0L, TraceId: CurrentTraceId())),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to terminalize a chat turn interrupted before run ownership. RequestId={RequestId}", correlation.RequestId);
        }
    }

    // W3C trace id of the ambient activity at teardown (for cross-correlation with exported traces), or null when none
    // is in scope. A default (all-zero) id is treated as absent. Mirrors the pump's interrupted-path trace capture.
    private static string? CurrentTraceId()
    {
        if (Activity.Current is not { } activity)
        {
            return null;
        }

        var traceId = activity.TraceId;
        return traceId == default ? null : traceId.ToString();
    }
}
