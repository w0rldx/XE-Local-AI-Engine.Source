namespace XE_Local_AI_Engine.Client.Services.Integrations.Implementation;

using System.Diagnostics;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Chat;

/// <summary>
///     The admin reads over integration executions, and the ONE cancel primitive both the operator surface and the
///     external route call.
/// </summary>
/// <remarks>
///     No interface: a one-implementation interface is scaffolding, and the endpoints are as testable against this class,
///     whose own collaborators are all interfaces. It is <c>public</c> rather than <c>internal</c> because the Client
///     endpoints live in another assembly. The cancel is NOT key-scoped — an operator cancelling from the admin UI is not
///     acting as an integrator and must reach every row, so it deliberately does not go through
///     <see cref="IntegrationExternalAccess" />; the external route applies that rule itself before it calls here.
/// </remarks>
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

    /// <summary>
    ///     The total the operator's pager labels a page with: the same filter, without its window. A second read rather
    ///     than a field on the list result, because the sweep and the prior-outputs replay both list without ever
    ///     wanting to pay for a COUNT.
    /// </summary>
    public Task<int> CountAsync(IntegrationExecutionFilter filter, CancellationToken cancellationToken = default) =>
        _executions.CountAsync(filter, cancellationToken);

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

    /// <summary>Requests cancellation, in the fixed order the transition table needs.</summary>
    /// <remarks>
    ///     Stamp the durable stop marker ONCE, terminalize a row that has not started in ONE transaction, then signal the
    ///     registered token on EVERY path. A step-2 CAS lost to the coordinator is not a dropped cancel: the marker is
    ///     already durable, the signal has already fired, and the coordinator's pre-run re-read plus its cancellable run
    ///     token both catch it. Why each step is shaped that way: ADR 0008 ("The cancel primitive's fixed order").
    /// </remarks>
    public async Task<IntegrationCancelOutcome> RequestCancelAsync(Guid executionId, CancellationToken cancellationToken = default)
    {
        var execution = await _executions.GetByIdAsync(executionId, cancellationToken);
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
            // Idempotent: a row whose marker is already durable needs no second write, because re-stamping bumps the version out from under the
            // coordinator's bounded terminal retries. The signal in the finally below still fires, which is what actually stops a run in flight.
            if (execution.StopRequestedAtUtc is not null)
            {
                return IntegrationCancelOutcome.Requested;
            }

            // CancellationToken.None from here down: a cancel that has decided to stop a run must finish stamping and closing it even if the client walks
            // away. NewStatus equal to the current status makes this a pure marker write under the same CAS, so it cannot resurrect a just-terminalized row.
            var marked = await _executions.UpdateStatusAsync(new IntegrationExecutionStatusUpdate
                {
                    ExecutionId = executionId,
                    ExpectedVersion = execution.Version,
                    ExpectedStatuses = new HashSet<IntegrationExecutionStatus>
                    {
                        execution.Status
                    },
                    NewStatus = execution.Status,
                    StartedAtUtc = null,
                    EndedAtUtc = null,
                    InvocationId = null,
                    StopRequestedAtUtc = nowUnixMs
                },
                CancellationToken.None);

            if (!marked)
            {
                // The CAS lost. Either the coordinator advanced the row a moment ago and the signal below still
                // reaches it, or the row terminalized — and a cancel on a finished run is a 409, not a 202.
                var fresh = await _executions.GetByIdAsync(executionId, CancellationToken.None);
                if (fresh is null)
                {
                    return IntegrationCancelOutcome.NotFound;
                }

                if (fresh.Status is not (IntegrationExecutionStatus.Accepted or IntegrationExecutionStatus.Queued or IntegrationExecutionStatus.Running))
                {
                    return IntegrationCancelOutcome.AlreadyTerminal;
                }
            }
            else if (execution.Status is IntegrationExecutionStatus.Accepted or IntegrationExecutionStatus.Queued
                     && !await TryTerminalizeCancelledAsync(execution, execution.Version + 1, nowUnixMs, CancellationToken.None))
            {
                // The terminal CAS lost. Lost to a TERMINAL row — a pre-run rejection that beat this cancel to it — the run is over and the honest answer is
                // a 409, not a 202 the caller will poll for a cancel that never arrives. A still-live row is the ordinary case and stays a 202.
                var fresh = await _executions.GetByIdAsync(executionId, CancellationToken.None);
                if (fresh is not null
                    && fresh.Status is not (IntegrationExecutionStatus.Accepted or IntegrationExecutionStatus.Queued or IntegrationExecutionStatus.Running))
                {
                    return IntegrationCancelOutcome.AlreadyTerminal;
                }
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

    /// <summary>
    ///     <see langword="true" /> when this cancel won the terminal compare-and-swap and owns the artefacts, and
    ///     <see langword="false" /> when it did not — which the caller resolves against the row itself, because a loss
    ///     to a terminal row and a loss to a live one are different answers.
    /// </summary>
    private async Task<bool> TryTerminalizeCancelledAsync(IntegrationExecutionSnapshot execution,
        long expectedVersion,
        long nowUnixMs,
        CancellationToken cancellationToken)
    {
        // An entry a previous process created, or one the ring already evicted, cannot carry an event. Seeding it from the persisted watermark is idempotent
        // and keeps the buffer the sole minter; if the ring refuses, the marker and the signal stand and the coordinator terminalizes on its pre-run re-read.
        if (!_buffer.TryCreate(execution.Id, execution.LastSequence))
        {
            _logger.LogWarning("The event buffer refused an entry for integration execution {ExecutionId}; the cancel marker stands and the coordinator will terminalize it.", execution.Id);
            return false;
        }

        // The audit row is built BEFORE the terminal command and carried inside it, so the store inserts it in the
        // same transaction. Written only if the CAS below wins, because a lost CAS rolls that transaction back.
        var trigger = await _triggers.GetByIdAsync(execution.TriggerId, cancellationToken);
        var audit = new IntegrationInvocationAuditInput
        {
            InvocationId = execution.InvocationId,
            RequestId = execution.RequestId,
            TriggerName = trigger?.Name ?? execution.TriggerId.ToString("D"),
            KeyPrefix = execution.KeyPrefix,
            TargetAgentDefinitionId = trigger?.TargetAgentDefinitionId ?? Guid.Empty,
            TerminalStatus = NodeChatMessageStatusValues.Cancelled,
            TraceId = Activity.Current?.TraceId.ToString(),
            LatencyMs = Math.Max(val1: 0L, nowUnixMs - execution.ReceivedAtUtc)
        };

        var sequence = _buffer.Reserve(execution.Id);
        var published = false;
        try
        {
            var won = await _executions.TryTerminalizeAsync(new IntegrationTerminalizeCommand
                {
                    ExecutionId = execution.Id,
                    ExpectedVersion = expectedVersion,
                    ExpectedStatuses = new HashSet<IntegrationExecutionStatus>
                    {
                        IntegrationExecutionStatus.Accepted,
                        IntegrationExecutionStatus.Queued
                    },
                    NewStatus = IntegrationExecutionStatus.Cancelled,
                    Sequence = sequence,
                    EventType = IntegrationStreamEventTypes.ExecutionCancelled,
                    EndedAtUtc = nowUnixMs,
                    FailureCategory = null,
                    FailureSummary = null,
                    EventDetailJson = null,
                    Audit = audit
                },
                cancellationToken);
            if (!won)
            {
                return false;
            }

            _buffer.Publish(new IntegrationStreamEvent
            {
                Type = IntegrationStreamEventTypes.ExecutionCancelled,
                Sequence = sequence,
                ExecutionId = execution.Id,
                SessionId = execution.SessionId,
                OccurredAtUtc = nowUnixMs,
                ContentType = null,
                Payload = null
            });
            published = true;
            return true;
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
