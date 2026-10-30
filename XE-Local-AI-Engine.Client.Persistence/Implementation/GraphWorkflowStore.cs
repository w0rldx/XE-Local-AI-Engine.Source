namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The Graph Workflow substrate: one store, because this store has one family — definitions, and the runs, node
///     runs and events below them. Partial files split it by area; the mutation helpers and snapshots live here.
/// </summary>
/// <remarks>
///     Every run mutation takes the run row inside one transaction, which is what makes the single <c>seq</c> counter
///     safe: two writers cannot allocate the same watermark, and neither can skip one. simplified: that row is then the
///     lock for its whole subtree, so writes across parallel node runs of one run serialize on it. Accepted — the
///     runtime advances one run at a time behind a single gate, and SQLite runs WAL with a busy timeout. Upgrade path
///     if contention ever shows: per-node-run sequence namespaces merged on read.
/// </remarks>
public sealed partial class GraphWorkflowStore : IGraphWorkflowStore
{
    /// <summary>camelCase, matching every other document this product puts on a wire — these details are READ by name.</summary>
    private static readonly JsonSerializerOptions JsonOptions = JsonSerializerOptions.Web;

    /// <summary>The node-run statuses a host death strands: exactly the two that had an executor behind them.</summary>
    private static readonly GraphWorkflowNodeRunStatus[] InterruptedStatuses =
    [
        GraphWorkflowNodeRunStatus.Queued,
        GraphWorkflowNodeRunStatus.Running
    ];

    /// <summary>The run statuses that still pin a definition. Anything else has finished and kept its own pinned graph.</summary>
    private static readonly GraphWorkflowRunStatus[] LiveRunStatuses =
    [
        GraphWorkflowRunStatus.Pending,
        GraphWorkflowRunStatus.Running,
        GraphWorkflowRunStatus.WaitingForApproval,
        GraphWorkflowRunStatus.Cancelling
    ];

    /// <summary>
    ///     The run statuses that occupy a concurrency slot — the ones a node is actually carrying.
    /// </summary>
    /// <remarks>
    ///     <c>Running</c> has work in flight and <c>Cancelling</c> is still draining. <c>WaitingForApproval</c> counts
    ///     only while a row of it is queued or running: a PARKED run releases its slot (the query's second filter).
    ///     <b><c>Pending</c> is deliberately absent</b>, which separates
    ///     this from <see cref="LiveRunStatuses" />: Pending is the queue admission draws FROM, so counting it would
    ///     count the run asking to start against its own admission and a cap of one would admit nothing at all.
    /// </remarks>
    private static readonly GraphWorkflowRunStatus[] ExecutingRunStatuses =
    [
        GraphWorkflowRunStatus.Running,
        GraphWorkflowRunStatus.WaitingForApproval,
        GraphWorkflowRunStatus.Cancelling
    ];

    private readonly NodeChatDbContext _dbContext;
    private readonly TimeProvider _timeProvider;

    public GraphWorkflowStore(NodeChatDbContext dbContext, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _dbContext = dbContext;
        _timeProvider = timeProvider;
    }

    /// <summary>One decision, one event, one transaction: what every run mutation on this store is.</summary>
    private Task<GraphWorkflowMutationResult> ExecuteMutationAsync(Guid runId,
        long expectedVersion,
        Func<GraphWorkflowRun, MutationOutcome> mutate,
        CancellationToken cancellationToken) =>
        ExecuteMutationAsync(runId, expectedVersion, run => Task.FromResult(mutate(run)), cancellationToken);

    private async Task<GraphWorkflowMutationResult> ExecuteMutationAsync(Guid runId,
        long expectedVersion,
        Func<GraphWorkflowRun, Task<MutationOutcome>> mutate,
        CancellationToken cancellationToken) =>
        await ExecuteConditionalMutationAsync(runId, expectedVersion, mutate, cancellationToken) switch
        {
            GraphWorkflowConditionalWriteResult.Written written => written.Result,

            // This overload's mutations always decide; a decline here is a wiring bug, not a race.
            var declined => throw new InvalidOperationException($"An unconditional graph workflow mutation on run '{runId}' declined: {declined}.")
        };

    /// <summary>
    ///     The same one-decision-one-event-one-transaction shape, for a mutation that may decline to write: a
    ///     <see cref="MutationOutcome.Decline" /> outcome rolls the transaction back untouched and answers
    ///     <see cref="GraphWorkflowConditionalWriteResult.Declined" />, which is how a compare-and-set reports that it
    ///     matched no row without raising.
    /// </summary>
    private async Task<GraphWorkflowConditionalWriteResult> ExecuteConditionalMutationAsync(Guid runId,
        long expectedVersion,
        Func<GraphWorkflowRun, Task<MutationOutcome>> mutate,
        CancellationToken cancellationToken,
        bool bumpVersion = true)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var run = await _dbContext.GraphWorkflowRuns.SingleOrDefaultAsync(entity => entity.Id == runId, cancellationToken)
                      ?? throw new GraphWorkflowNotFoundException($"Graph workflow run '{runId}' was not found.");
            EnsureVersion(run, expectedVersion);

            var outcome = await mutate(run);
            if (outcome.DeclinedBy is { } reason)
            {
                await RollbackAsync(transaction);
                return new GraphWorkflowConditionalWriteResult.Declined
                {
                    Reason = reason
                };
            }

            // The watermark is allocated per COMMIT, not per event row: a mutation that records no event — the settle from Cancelling to Cancelled is the only one —
            // still MOVED the run. The events feed pages WHERE seq > afterSeq over event ROWS, so the number this skips is simply a number no row ever carried.
            var sequence = outcome.EventType is { } eventType ? AddEvent(run, eventType, outcome.NodeKey, outcome.DetailJson) : ++run.Seq;
            if (bumpVersion)
            {
                run.Version++;
            }

            _ = await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new GraphWorkflowConditionalWriteResult.Written
            {
                Result = new GraphWorkflowMutationResult
                {
                    RunId = runId,
                    Sequence = sequence
                }
            };
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // The version check above answers the caller that read a stale number. This is the other half: two writers that each read version N both pass it, and
            // only the token stops the later one from overwriting the earlier without either learning of the other.
            await RollbackAsync(transaction);
            throw new GraphWorkflowInvalidTransitionException($"A concurrent writer moved graph workflow run '{runId}' before this write could commit.", exception);
        }
        catch
        {
            await RollbackAsync(transaction);
            throw;
        }
    }

    private long AddEvent(GraphWorkflowRun run, string eventType, string? nodeKey, byte[]? detailJson)
    {
        var sequence = ++run.Seq;
        _dbContext.GraphWorkflowRunEvents.Add(new GraphWorkflowRunEvent
        {
            Id = Guid.NewGuid(),
            RunId = run.Id,
            Seq = sequence,
            EventType = eventType,
            NodeKey = nodeKey,
            DetailJson = detailJson,
            CreatedAtUtc = Now()
        });
        return sequence;
    }

    private static void EnsureVersion(GraphWorkflowRun run, long expectedVersion)
    {
        if (expectedVersion == GraphWorkflowVersions.Any || run.Version == expectedVersion)
        {
            return;
        }

        throw new GraphWorkflowInvalidTransitionException($"The graph workflow run version is stale (expected {expectedVersion}, current {run.Version}).");
    }

    /// <summary>The event type a node-run status move records, so no call site has to restate it.</summary>
    private static string EventTypeFor(GraphWorkflowNodeRunStatus status) =>
        status switch
        {
            // The collapse a restart writes. A retry-in-place overrides this with node.retried, which is the one move
            // the status alone cannot tell apart from it.
            GraphWorkflowNodeRunStatus.Pending => GraphWorkflowEventTypes.NodeInterrupted,
            GraphWorkflowNodeRunStatus.Queued => GraphWorkflowEventTypes.NodeQueued,
            GraphWorkflowNodeRunStatus.Running => GraphWorkflowEventTypes.NodeStarted,
            GraphWorkflowNodeRunStatus.WaitingForApproval => GraphWorkflowEventTypes.GateRequested,
            GraphWorkflowNodeRunStatus.Succeeded => GraphWorkflowEventTypes.NodeCompleted,
            GraphWorkflowNodeRunStatus.Failed => GraphWorkflowEventTypes.NodeFailed,
            GraphWorkflowNodeRunStatus.Skipped => GraphWorkflowEventTypes.NodeSkipped,
            _ => GraphWorkflowEventTypes.NodeCancelled
        };

    /// <summary>
    ///     The event a run status move records, or <see langword="null" /> for the one move that records none.
    /// </summary>
    /// <remarks>
    ///     <c>Cancelling</c> gets the event of the thing it has BEGUN, because a reader following the log has to see
    ///     the cancel at the moment it was asked for. The settle after it is the run row's business rather than a
    ///     second event, so <c>Cancelling → Cancelled</c> writes nothing and the log carries exactly one
    ///     <c>run.cancelled</c> per cancel. A run reaching <c>Cancelled</c> from anywhere else announced none, so that
    ///     move still writes its own.
    /// </remarks>
    private static string? EventTypeFor(GraphWorkflowRunStatus previousStatus, GraphWorkflowRunStatus status) =>
        previousStatus == GraphWorkflowRunStatus.Cancelling && status == GraphWorkflowRunStatus.Cancelled
            ? null
            : EventTypeFor(status);

    private static string EventTypeFor(GraphWorkflowRunStatus status) =>
        status switch
        {
            GraphWorkflowRunStatus.Running => GraphWorkflowEventTypes.RunStarted,
            GraphWorkflowRunStatus.WaitingForApproval => GraphWorkflowEventTypes.RunWaiting,
            GraphWorkflowRunStatus.Cancelling or GraphWorkflowRunStatus.Cancelled => GraphWorkflowEventTypes.RunCancelled,
            GraphWorkflowRunStatus.Completed => GraphWorkflowEventTypes.RunCompleted,
            GraphWorkflowRunStatus.Failed => GraphWorkflowEventTypes.RunFailed,

            // Pending: a run is created Pending and never moves back to it, so nothing reaches here today.
            _ => GraphWorkflowEventTypes.RunCreated
        };

    /// <summary>
    ///     A node-run status nothing further will happen to on its own. The Application layer's state machine is the
    ///     authority on what a legal MOVE is; this is only what stamps a completion instant, which the database can see
    ///     for itself.
    /// </summary>
    private static bool IsTerminal(GraphWorkflowNodeRunStatus status) =>
        status is GraphWorkflowNodeRunStatus.Succeeded
            or GraphWorkflowNodeRunStatus.Failed
            or GraphWorkflowNodeRunStatus.Skipped
            or GraphWorkflowNodeRunStatus.Cancelled;

    private static byte[]? ReasonDetail(string? sanitizedReason) =>
        string.IsNullOrWhiteSpace(sanitizedReason) ? null : Utf8(JsonSerializer.Serialize(new ReasonDetailPayload(sanitizedReason), JsonOptions));

    private static GraphWorkflowRunSnapshot RunSnapshot(GraphWorkflowRun run) =>
        new()
        {
            Id = run.Id,
            RequestId = run.RequestId,
            DefinitionId = run.DefinitionId,
            DefinitionVersion = run.DefinitionVersion,
            GraphHash = run.GraphHash,
            Status = run.Status,
            FailureClass = run.FailureClass,
            GraphJson = Text(run.GraphJson),
            InputJson = TextOrNull(run.InputJson),
            OutputJson = TextOrNull(run.OutputJson),
            PreviousJson = TextOrNull(run.PreviousJson),
            Seq = run.Seq,
            Version = run.Version,
            CancelRequestedAtUtc = run.CancelRequestedAtUtc,
            StartedAtUtc = run.StartedAtUtc,
            CompletedAtUtc = run.CompletedAtUtc,
            CreatedAtUtc = run.CreatedAtUtc,
            ConversationId = run.ConversationId,
            TriggerMessageId = run.TriggerMessageId,
            TriggerSource = run.TriggerSource,
            PrincipalId = run.PrincipalId,
            KeyPrefix = run.KeyPrefix
        };

    private static GraphWorkflowNodeRunSnapshot NodeRunSnapshot(GraphWorkflowNodeRun nodeRun) =>
        new()
        {
            Id = nodeRun.Id,
            RunId = nodeRun.RunId,
            NodeKey = nodeRun.NodeKey,
            Kind = nodeRun.Kind,
            Status = nodeRun.Status,
            Attempt = nodeRun.Attempt,
            PendingDecisionKind = nodeRun.PendingDecisionKind,
            DecisionOperationId = nodeRun.DecisionOperationId,
            DecidedBySubject = TextOrNull(nodeRun.DecidedBySubject),
            FailureClass = nodeRun.FailureClass,
            Error = TextOrNull(nodeRun.Error),
            InputJson = TextOrNull(nodeRun.InputJson),
            OutputJson = TextOrNull(nodeRun.OutputJson),
            InvocationId = nodeRun.InvocationId,
            StartedAtUtc = nodeRun.StartedAtUtc,
            CompletedAtUtc = nodeRun.CompletedAtUtc,
            UpdatedAtUtc = nodeRun.UpdatedAtUtc,
            PublishedMessageId = nodeRun.PublishedMessageId,
            Steering = SteeringOf(nodeRun.SteeringJson)
        };

    private async Task RollbackAsync(IDbContextTransaction transaction)
    {
        await transaction.RollbackAsync(CancellationToken.None);
        _dbContext.ChangeTracker.Clear();
    }

    private static byte[]? Utf8OrNull(string? value) =>
        value is null ? null : Encoding.UTF8.GetBytes(value);

    private static string? TextOrNull(byte[]? value) =>
        value is null ? null : Encoding.UTF8.GetString(value);

    /// <summary>
    ///     What one mutation decided. A null <see cref="EventType" /> is the deliberate "no event row": the move
    ///     happened, the run version bumps, and the watermark stays where the last event left it.
    /// </summary>
    private sealed record MutationOutcome
    {
        public required string? EventType { get; init; }

        public required string? NodeKey { get; init; }

        public required byte[]? DetailJson { get; init; }

        /// <summary>Set only by <see cref="Decline" />: the conditional write writes nothing and reports why.</summary>
        public GraphWorkflowWriteDecline? DeclinedBy { get; private init; }

        public static MutationOutcome Decline(GraphWorkflowWriteDecline reason) =>
            new()
            {
                EventType = null,
                NodeKey = null,
                DetailJson = null,
                DeclinedBy = reason
            };
    }

    private sealed record ReasonDetailPayload(string Reason);

    /// <summary>The gate.decided detail: which pause, and what was answered. Read by name, so camelCase like every other.</summary>
    private sealed record GateDecidedDetailPayload(string NodeKey, string Decision);

    /// <summary>
    ///     Whether a failed write was the unique index refusing a duplicate. SQLite reports both PRIMARY KEY (1555) and
    ///     UNIQUE (2067) as extended codes of the generic constraint error, and only those two mean "already exists".
    /// </summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteExtendedErrorCode: 1555 or 2067 };

    private static void EnsureNotBlank(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("The value cannot be null, empty, or whitespace.", parameterName);
        }
    }

    private long Now() =>
        _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private static byte[] Utf8(string value) =>
        Encoding.UTF8.GetBytes(value);

    private static string Text(byte[] value) =>
        Encoding.UTF8.GetString(value);
}
