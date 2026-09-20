namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using System.Diagnostics;

/// <summary>
///     Terminalizes a just-created assistant row if the turn is torn down before run ownership is established.
/// </summary>
/// <remarks>
///     It is armed right after the placeholder or variant is persisted and disarmed by
///     <see cref="OwnershipEstablished" /> once the pump and runner exist. Disposal runs on any exit — fall-through,
///     an exception, or async-iterator disposal on disconnect — so a pre-ownership disconnect never strands the row
///     until the restart reaper. Both local front doors share it, so their teardown behaves identically.
/// </remarks>
internal sealed class PreOwnershipTerminalizationGuard : IAsyncDisposable
{
    // Terminal error stamped when a turn is torn down (client disconnect/cancel) before run ownership was established.
    // Mirrors the Interrupted terminal the restart recovery service assigns to rows orphaned by a crash.
    private const string PreOwnershipInterruptedError = "Interrupted before the response started.";
    private readonly INodeChatPersistenceService _persistence;
    private readonly NodeChatMessageCorrelation _correlation;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    private bool _ownershipEstablished;

    public PreOwnershipTerminalizationGuard(
        INodeChatPersistenceService persistence,
        NodeChatMessageCorrelation correlation,
        TimeProvider timeProvider,
        ILogger logger)
    {
        _persistence = persistence;
        _correlation = correlation;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public void OwnershipEstablished()
    {
        _ownershipEstablished = true;
    }

    /// <summary>
    ///     Disarms interruption cleanup after the caller has already persisted a deliberate pre-ownership terminal.
    ///     This preserves that winning Failed/Cancelled status instead of attempting a second Interrupted write.
    /// </summary>
    public void TerminalizationHandled()
    {
        _ownershipEstablished = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (_ownershipEstablished)
        {
            return;
        }

        // Best-effort on a fresh token, since the client token that triggered this teardown is already cancelled. A
        // missing or mismatched row only logs: this path must never throw out of an iterator disposal.
        try
        {
            // A thin run envelope rides along so this terminal row gets one in the SAME transaction, or this would be
            // the one live path writing a terminal without it. No InvocationState exists here, so the detail is empty.
            await _persistence.TerminalizeAssistantMessageAsync(new NodeChatTerminalizeMessageRequest
            {
                Correlation = _correlation,
                Status = NodeChatMessageStatusValues.Interrupted,
                UpdatedAtUtc = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds(),
                Error = PreOwnershipInterruptedError,
                Envelope = new AgentRunEnvelopeMetadata { InvocationId = null, DurationMs = 0L, TraceId = CurrentTraceId() }
            },
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to terminalize a chat turn interrupted before run ownership. RequestId={RequestId}", _correlation.RequestId);
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
