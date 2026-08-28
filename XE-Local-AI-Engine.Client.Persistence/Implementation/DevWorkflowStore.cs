namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     The durable substrate for development workflows. Every mutation takes the run row inside one transaction, which
///     is what makes the single <c>last_sequence</c> counter safe: two writers cannot allocate the same watermark, and
///     neither can skip one.
/// </summary>
/// <remarks>
///     ponytail: the run row is the lock for its whole subtree, so writes across parallel node-runs of one run
///     serialize on it. Accepted — the runtime already serializes agent nodes on one slot and bounds sandbox nodes, and
///     SQLite runs WAL with a busy timeout. Upgrade path if contention ever shows: per-node-run sequence namespaces
///     merged on read.
/// </remarks>
internal sealed partial class DevWorkflowStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : IDevWorkflowStore
{
    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    private async Task<DevWorkflowMutationResult> ExecuteMutationAsync(Guid runId,
        long expectedVersion,
        Guid? operationId,
        Func<DevWorkflowRun, Task<MutationOutcome>> mutate,
        CancellationToken cancellationToken)
    {
        // Query-first, never insert-then-catch: a caught unique-index violation leaves an Added entity in the change
        // tracker that every later write in the same scope would trip over.
        if (operationId is { } preflight && await FindOperationAsync(runId, preflight, cancellationToken).ConfigureAwait(false) is { } recorded)
        {
            return recorded;
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (operationId is { } inTransaction && await FindOperationAsync(runId, inTransaction, cancellationToken).ConfigureAwait(false) is { } alreadyRecorded)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return alreadyRecorded;
            }

            var run = await _dbContext.DevWorkflowRuns.SingleOrDefaultAsync(entity => entity.Id == runId, cancellationToken).ConfigureAwait(false)
                      ?? throw new DevWorkflowNotFoundException($"Development workflow run '{runId}' was not found.");
            EnsureVersion(run, expectedVersion);

            var outcome = await mutate(run).ConfigureAwait(false);
            var sequence = AddEvent(run, outcome.EventType, outcome.NodeRunId, outcome.Outcome, operationId, outcome.DetailJson);
            run.Version++;
            run.UpdatedAtUtc = Now();
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DevWorkflowMutationResult(runId, sequence, run.Version, run.Status, run.GraphRevision, outcome.SupersededArtifactId);
        }
        catch (DbUpdateException exception)
        {
            await RollbackAsync(transaction).ConfigureAwait(false);

            // Belt to the in-transaction check's braces. On SQLite that check already wins every real race — EF opens
            // transactions as BEGIN IMMEDIATE, so a second writer blocks on the writer lock and sees the recorded
            // operation before writing anything, and this branch is measurably never entered. It stays because it is
            // the honest answer to the question the catch asks: if the write that beat us used the SAME operation id,
            // the caller wants that result, not an exception. Only a real version mismatch, or a different operation,
            // still throws.
            if (operationId is { } contested && await FindOperationAsync(runId, contested, cancellationToken).ConfigureAwait(false) is { } settled)
            {
                return settled;
            }

            throw new DevWorkflowConcurrencyException("A concurrent writer won the race before the development workflow mutation committed.", exception);
        }
        catch
        {
            await RollbackAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    ///     The event already recorded for this operation, rebuilt against the run row as it stands now — a replayed step
    ///     wants the version it should continue from, not the one the first attempt saw.
    /// </summary>
    private async Task<DevWorkflowMutationResult?> FindOperationAsync(Guid runId, Guid operationId, CancellationToken cancellationToken)
    {
        var recorded = await _dbContext.DevWorkflowRunEvents.AsNoTracking()
                                       .SingleOrDefaultAsync(entity => entity.RunId == runId && entity.OperationId == operationId, cancellationToken)
                                       .ConfigureAwait(false);
        if (recorded is null)
        {
            return null;
        }

        var run = await _dbContext.DevWorkflowRuns.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == runId, cancellationToken).ConfigureAwait(false);
        return run is null
            ? null
            : new DevWorkflowMutationResult(runId, recorded.Sequence, run.Version, run.Status, run.GraphRevision, RecordedSupersededArtifactId(recorded));
    }

    /// <summary>
    ///     The superseded id a recorded artifact append reported, read back off its event. Without it a replayed append
    ///     answers <see langword="null" /> where the first call answered an id, and the caller that owns the blob store
    ///     would skip a sweep it still has to do — a replay has to return the recorded result, not a thinner one.
    /// </summary>
    private static Guid? RecordedSupersededArtifactId(DevWorkflowRunEvent recorded)
    {
        if (recorded.EventType != DevWorkflowEventTypes.ArtifactSuperseded || recorded.DetailJson is null)
        {
            return null;
        }

        return JsonSerializer.Deserialize<ArtifactSupersessionDetail>(recorded.DetailJson)?.SupersededArtifactId;
    }

    private long AddEvent(DevWorkflowRun run, string eventType, Guid? nodeRunId, string? outcome, Guid? operationId, byte[]? detailJson)
    {
        var sequence = NextSequence(run);
        _dbContext.DevWorkflowRunEvents.Add(new DevWorkflowRunEvent
        {
            Id = Guid.NewGuid(),
            RunId = run.Id,
            NodeRunId = nodeRunId,
            Sequence = sequence,
            EventType = eventType,
            DetailJson = detailJson,
            OperationId = operationId,
            Outcome = outcome,
            OccurredAtUtc = Now()
        });
        return sequence;
    }

    private static long NextSequence(DevWorkflowRun run) =>
        ++run.LastSequence;

    private static void EnsureVersion(DevWorkflowRun run, long expectedVersion)
    {
        if (expectedVersion == DevWorkflowVersions.Any || run.Version == expectedVersion)
        {
            return;
        }

        throw new DevWorkflowConcurrencyException($"The development workflow run version is stale (expected {expectedVersion}, current {run.Version}).");
    }

    /// <summary>
    ///     The work item's status is written by the runtime inside the transaction that moves the run, never by a
    ///     client — which is the only way the two can never disagree.
    /// </summary>
    private async Task ApplyWorkItemStatusAsync(Guid workItemId, DevWorkflowWorkItemStatus? status, CancellationToken cancellationToken)
    {
        if (status is not { } target)
        {
            return;
        }

        var workItem = await _dbContext.DevWorkflowWorkItems.SingleOrDefaultAsync(entity => entity.Id == workItemId, cancellationToken).ConfigureAwait(false)
                       ?? throw new DevWorkflowNotFoundException($"Development workflow work item '{workItemId}' was not found.");
        if (workItem.Status == target)
        {
            return;
        }

        workItem.Status = target;
        workItem.Version++;
        workItem.UpdatedAtUtc = Now();
    }

    private async Task<DevWorkflowNodeRun> LoadNodeRunAsync(Guid runId, Guid nodeRunId, CancellationToken cancellationToken) =>
        await _dbContext.DevWorkflowNodeRuns.SingleOrDefaultAsync(entity => entity.Id == nodeRunId && entity.RunId == runId, cancellationToken).ConfigureAwait(false)
        ?? throw new DevWorkflowNotFoundException($"Development workflow node run '{nodeRunId}' was not found on run '{runId}'.");

    private async Task RollbackAsync(IDbContextTransaction transaction)
    {
        await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        _dbContext.ChangeTracker.Clear();
    }

    /// <summary>The event type the runtime would otherwise have to restate at every call site.</summary>
    private static string EventTypeFor(DevWorkflowNodeRunStatus status) =>
        status switch
        {
            // The only reason to move back to Pending is a re-attempt, which is exactly what this event records.
            DevWorkflowNodeRunStatus.Pending => DevWorkflowEventTypes.NodeRetryScheduled,
            DevWorkflowNodeRunStatus.Queued => DevWorkflowEventTypes.NodeQueued,
            DevWorkflowNodeRunStatus.Running => DevWorkflowEventTypes.NodeStarted,
            DevWorkflowNodeRunStatus.WaitingForApproval => DevWorkflowEventTypes.GateRequested,
            DevWorkflowNodeRunStatus.Blocked => DevWorkflowEventTypes.NodeInterventionRequired,
            DevWorkflowNodeRunStatus.Succeeded => DevWorkflowEventTypes.NodeCompleted,
            DevWorkflowNodeRunStatus.Failed => DevWorkflowEventTypes.NodeFailed,
            DevWorkflowNodeRunStatus.Skipped => DevWorkflowEventTypes.NodeSkipped,
            _ => DevWorkflowEventTypes.NodeCancelled
        };

    /// <summary>
    ///     The event a run status move records.
    ///     <para>
    ///         The two <c>-ing</c> statuses get the event of the thing they have BEGUN, not a generic one: a reader
    ///         following the log has to see the pause or the cancel at the moment it was asked for, and the settled
    ///         status that follows is the run row's business rather than a second event. <c>Running</c> is the one
    ///         genuinely ambiguous move — a first start and a resume differ only by whether the run has started before.
    ///     </para>
    /// </summary>
    private static string EventTypeFor(DevWorkflowRunStatus status, bool isFirstStart) =>
        status switch
        {
            DevWorkflowRunStatus.Running => isFirstStart ? DevWorkflowEventTypes.RunStarted : DevWorkflowEventTypes.RunResumed,
            DevWorkflowRunStatus.Pausing or DevWorkflowRunStatus.Paused => DevWorkflowEventTypes.RunPaused,
            DevWorkflowRunStatus.Cancelling or DevWorkflowRunStatus.Cancelled => DevWorkflowEventTypes.RunCancelled,
            DevWorkflowRunStatus.Completed => DevWorkflowEventTypes.RunCompleted,
            DevWorkflowRunStatus.Failed => DevWorkflowEventTypes.RunFailed,
            DevWorkflowRunStatus.WaitingForApproval => DevWorkflowEventTypes.RunWaiting,

            // Pending: a run is created Pending and never transitions back to it, so nothing reaches here today.
            _ => DevWorkflowEventTypes.RunResumed
        };

    /// <summary>
    ///     The outcome a run status move records, from the closed lowercase set the runtime owns — or null, which is
    ///     the honest answer for a move whose event type already says everything: a run that is now Running has no
    ///     "outcome" yet, and stamping one outside the closed set puts a token in the durable log that no consumer of
    ///     that vocabulary can read.
    /// </summary>
    private static string? OutcomeFor(DevWorkflowRunStatus status) =>
        status switch
        {
            DevWorkflowRunStatus.Completed => "succeeded",
            DevWorkflowRunStatus.Failed => "failed",
            DevWorkflowRunStatus.Cancelling or DevWorkflowRunStatus.Cancelled => "cancelled",
            _ => null
        };

    private static string? OutcomeFor(DevWorkflowNodeRunStatus status) =>
        status switch
        {
            DevWorkflowNodeRunStatus.Succeeded => "succeeded",
            DevWorkflowNodeRunStatus.Failed => "failed",
            DevWorkflowNodeRunStatus.Cancelled => "cancelled",
            _ => null
        };

    private static bool IsTerminal(DevWorkflowNodeRunStatus status) =>
        status is DevWorkflowNodeRunStatus.Succeeded or DevWorkflowNodeRunStatus.Failed or DevWorkflowNodeRunStatus.Skipped or DevWorkflowNodeRunStatus.Cancelled;

    /// <summary>SHA-256 of the graph bytes, computed here so the hash and the blob can never describe different graphs.</summary>
    private static string HashGraph(byte[] graphJson) =>
        Convert.ToHexStringLower(SHA256.HashData(graphJson));

    private static byte[]? ReasonDetail(string? sanitizedReason) =>
        string.IsNullOrWhiteSpace(sanitizedReason) ? null : Utf8(JsonSerializer.Serialize(new ReasonDetailPayload(sanitizedReason)));

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

    private static byte[]? Utf8OrNull(string? value) =>
        value is null ? null : Encoding.UTF8.GetBytes(value);

    private static string Text(byte[] value) =>
        Encoding.UTF8.GetString(value);

    private static string? TextOrNull(byte[]? value) =>
        value is null ? null : Encoding.UTF8.GetString(value);

    private sealed record MutationOutcome(string EventType, string? Outcome, byte[]? DetailJson, Guid? NodeRunId = null, Guid? SupersededArtifactId = null);

    private sealed record ReasonDetailPayload(string Reason);

    private sealed record MaterializationDetail(int NodeRunCount, int GraphRevision);

    private sealed record ArtifactSupersessionDetail(Guid SupersededArtifactId, string SupersededManagedReference, int Version);

    private sealed record ArtifactUseDetail(int ArtifactCount);

    private sealed record StaleMarkDetail(Guid SupersededArtifactId, Guid SupersedingArtifactId, int MarkedCount);

    private sealed record WorkSessionAttachedDetail(Guid WorkSessionId, int Attempt, int SessionResumes);
}
