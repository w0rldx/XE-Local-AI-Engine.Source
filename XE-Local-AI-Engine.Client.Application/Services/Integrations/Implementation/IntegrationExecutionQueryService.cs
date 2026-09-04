namespace XE_Local_AI_Engine.Client.Services.Integrations.Implementation;

using System.Diagnostics;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     The admin reads over integration executions, and the ONE cancel primitive both the operator surface and the
///     external route call.
///     <para>
///         <b>No interface.</b> A one-implementation interface neither the brief nor a ruling asked for is
///         scaffolding: the endpoints are as testable against this class, whose own collaborators are all interfaces.
///         It is <c>public</c> rather than <c>internal</c> because the Client endpoints live in another assembly.
///     </para>
///     <para>
///         <b>The cancel is not key-scoped.</b> An operator cancelling from the admin UI is not acting as an
///         integrator and must be able to reach every row, so this method deliberately does NOT go through
///         <see cref="IntegrationExternalAccess" />. The external route applies that rule itself, before it calls here.
///     </para>
/// </summary>
public sealed class IntegrationExecutionQueryService
{
    private readonly IIntegrationExecutionEventBuffer _buffer;
    private readonly IntegrationCancellationRegistry _cancellations;
    private readonly IIntegrationExecutionStore _executions;
    private readonly ILogger<IntegrationExecutionQueryService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IIntegrationTriggerStore _triggers;

    internal IntegrationExecutionQueryService(IIntegrationExecutionStore executions,
        IIntegrationTriggerStore triggers,
        IIntegrationExecutionEventBuffer buffer,
        IntegrationCancellationRegistry cancellations,
        TimeProvider timeProvider,
        ILogger<IntegrationExecutionQueryService> logger)
    {
        _executions = executions ?? throw new ArgumentNullException(nameof(executions));
        _triggers = triggers ?? throw new ArgumentNullException(nameof(triggers));
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        _cancellations = cancellations ?? throw new ArgumentNullException(nameof(cancellations));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<IReadOnlyList<IntegrationExecutionSnapshot>> ListAsync(IntegrationExecutionFilter filter, CancellationToken cancellationToken = default) =>
        _executions.ListAsync(filter, cancellationToken);

    public Task<IntegrationExecutionSnapshot?> GetAsync(Guid executionId, CancellationToken cancellationToken = default) =>
        _executions.GetByIdAsync(executionId, cancellationToken);

    /// <summary>
    ///     A page of an execution's PERSISTED events, ascending by sequence and never from the ring: the ring is
    ///     evictable and empty after a restart, so a timeline read from it would lose history a caller still needs.
    /// </summary>
    public Task<IReadOnlyList<IntegrationExecutionEventSnapshot>> ListEventsAsync(Guid executionId,
        long sinceSequence,
        int limit,
        CancellationToken cancellationToken = default) =>
        _executions.ListEventsAsync(executionId, sinceSequence, limit, cancellationToken);

    /// <summary>
    ///     Requests cancellation, in the fixed order the transition table needs.
    ///     <list type="number">
    ///         <item>Stamp the durable stop marker, so a restart cannot resurrect the run.</item>
    ///         <item>
    ///             Terminalize a row that has not started, in ONE transaction. Whoever's CAS wins owns the terminal
    ///             event and the one audit row; a loser appends nothing, because the coordinator won the
    ///             <c>Queued -&gt; Running</c> race and will produce them itself.
    ///         </item>
    ///         <item>
    ///             Signal the registered token on EVERY path, whether step 2 won or lost and whatever the row reads.
    ///             Signalling only for a running row leaves the coordinator blocked in its lease wait until the lease
    ///             comes free on its own, which is not a cancel a caller can observe.
    ///         </item>
    ///     </list>
    ///     A step-2 CAS lost to the coordinator is not a dropped cancel: the marker is already durable, the signal has
    ///     already fired, and the coordinator's pre-run re-read plus its cancellable run token both catch it.
    /// </summary>
    public async Task<IntegrationCancelOutcome> RequestCancelAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        var execution = await _executions.GetByIdAsync(executionId, cancellationToken).ConfigureAwait(false);
        if (execution is null)
        {
            return IntegrationCancelOutcome.NotFound;
        }

        if (execution.Status is not (IntegrationExecutionStatus.Accepted or IntegrationExecutionStatus.Queued or IntegrationExecutionStatus.Running))
        {
            return IntegrationCancelOutcome.AlreadyTerminal;
        }

        var nowUnixMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        try
        {
            // CancellationToken.None from here down, for the same reason the coordinator uses it: a cancel that has
            // decided to stop a run must finish stamping and closing it even if the client that asked walks away.
            //
            // NewStatus equal to the current status makes this a pure marker write under the same compare-and-swap, so
            // it cannot resurrect a row that terminalized a moment ago.
            var marked = await _executions.UpdateStatusAsync(new IntegrationExecutionStatusUpdate(executionId,
                                                  execution.Version,
                                                  new HashSet<IntegrationExecutionStatus>
                                                  {
                                                      execution.Status
                                                  },
                                                  execution.Status,
                                                  StartedAtUtc: null,
                                                  EndedAtUtc: null,
                                                  InvocationId: null,
                                                  nowUnixMs),
                                              CancellationToken.None)
                                          .ConfigureAwait(false);

            if (!marked)
            {
                // The CAS lost. Either the coordinator advanced the row a moment ago and the signal below still
                // reaches it, or the row terminalized — and a cancel on a finished run is a 409, not a 202.
                var fresh = await _executions.GetByIdAsync(executionId, CancellationToken.None).ConfigureAwait(false);
                if (fresh is null)
                {
                    return IntegrationCancelOutcome.NotFound;
                }

                if (fresh.Status is not (IntegrationExecutionStatus.Accepted or IntegrationExecutionStatus.Queued or IntegrationExecutionStatus.Running))
                {
                    return IntegrationCancelOutcome.AlreadyTerminal;
                }
            }
            else if (execution.Status is IntegrationExecutionStatus.Accepted or IntegrationExecutionStatus.Queued)
            {
                await TryTerminalizeCancelledAsync(execution, execution.Version + 1, nowUnixMs, CancellationToken.None).ConfigureAwait(false);
            }

            return IntegrationCancelOutcome.Requested;
        }
        finally
        {
            // Step 3 runs on EVERY path, throws included. A queued row that was never signalled sits in its lease wait
            // until MaxQueueAgeSeconds, because the durable marker is only honoured at the post-lease re-check.
            _ = _cancellations.Signal(executionId);
        }
    }

    private async Task TryTerminalizeCancelledAsync(IntegrationExecutionSnapshot execution,
        long expectedVersion,
        long nowUnixMs,
        CancellationToken cancellationToken)
    {
        // An entry that a previous process created, or one the ring already evicted, cannot carry an event. Seeding it
        // from the persisted watermark is idempotent and keeps the buffer the sole minter; if the ring refuses, the
        // marker and the signal still stand and the coordinator terminalizes the row on its pre-run re-read.
        if (!_buffer.TryCreate(execution.Id, execution.LastSequence))
        {
            _logger.LogWarning("The event buffer refused an entry for integration execution {ExecutionId}; the cancel marker stands and the coordinator will terminalize it.", execution.Id);
            return;
        }

        // The audit row is built BEFORE the terminal command and carried inside it, so the store inserts it in the
        // same transaction. Written only if the CAS below wins, because a lost CAS rolls that transaction back.
        var trigger = await _triggers.GetByIdAsync(execution.TriggerId, cancellationToken).ConfigureAwait(false);
        var audit = new IntegrationInvocationAuditInput(execution.InvocationId,
            execution.RequestId,
            trigger?.Name ?? execution.TriggerId.ToString("D"),
            execution.KeyPrefix,
            trigger?.TargetAgentDefinitionId ?? Guid.Empty,
            NodeChatMessageStatusValues.Cancelled,
            Activity.Current?.TraceId.ToString(),
            Math.Max(val1: 0L, nowUnixMs - execution.ReceivedAtUtc));

        var sequence = _buffer.Reserve(execution.Id);
        var published = false;
        try
        {
            var won = await _executions.TryTerminalizeAsync(new IntegrationTerminalizeCommand(execution.Id,
                                               expectedVersion,
                                               new HashSet<IntegrationExecutionStatus>
                                               {
                                                   IntegrationExecutionStatus.Accepted,
                                                   IntegrationExecutionStatus.Queued
                                               },
                                               IntegrationExecutionStatus.Cancelled,
                                               sequence,
                                               IntegrationStreamEventTypes.ExecutionCancelled,
                                               nowUnixMs,
                                               FailureCategory: null,
                                               FailureSummary: null,
                                               EventDetailJson: null,
                                               audit),
                                           cancellationToken)
                                       .ConfigureAwait(false);
            if (!won)
            {
                return;
            }

            _buffer.Publish(new IntegrationStreamEvent(IntegrationStreamEventTypes.ExecutionCancelled,
                sequence,
                execution.Id,
                execution.SessionId,
                nowUnixMs,
                ContentType: null,
                Payload: null));
            published = true;
        }
        finally
        {
            if (!published)
            {
                // An unresolved reservation is not a hole readers tolerate; it parks every reader on this execution at
                // the barrier until the entry is evicted.
                _buffer.Abandon(execution.Id, sequence);
            }
        }
    }
}

/// <summary>What a cancel decided. Each value maps to exactly one status at both the admin and the external route.</summary>
public enum IntegrationCancelOutcome
{
    /// <summary>The stop marker is durable and the run was signalled. 202.</summary>
    Requested,

    /// <summary>No row with that id. 404.</summary>
    NotFound,

    /// <summary>The run had already finished. 409.</summary>
    AlreadyTerminal
}
