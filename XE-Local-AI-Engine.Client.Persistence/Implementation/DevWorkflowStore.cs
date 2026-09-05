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
    /// <summary>
    ///     camelCase, matching the Application layer — which has always serialized its own event details with the Web
    ///     defaults — and every other document this product puts on a wire.
    ///     <para>
    ///         Not optional and not cosmetic: these payloads are READ by name. Serialized with the framework default
    ///         the store wrote <c>{"WorkSessionId":…,"Attempt":1}</c> while the client looked for
    ///         <c>workSessionId</c> / <c>attempt</c>, so the attempt walk and the transcript link silently saw nothing
    ///         at all. The log is append-only, so rows written before this stay PascalCase for ever and the readers
    ///         take either spelling.
    ///     </para>
    /// </summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    /// <summary>One decision, one event, one transaction: what almost every command on this store is.</summary>
    private Task<DevWorkflowMutationResult> ExecuteMutationAsync(Guid runId,
        long expectedVersion,
        Guid? operationId,
        Func<DevWorkflowRun, Task<MutationOutcome>> mutate,
        CancellationToken cancellationToken) =>
        ExecuteMutationAsync(runId,
            expectedVersion,
            operationId,
            async run => (IReadOnlyList<MutationOutcome>)[await mutate(run).ConfigureAwait(false)],
            cancellationToken);

    /// <summary>
    ///     One transaction covering SEVERAL events, for a decision that is not one row's. The operation id goes on the
    ///     FIRST event and the result names its sequence, so a replay answers exactly what the first call answered and
    ///     every later row the same decision wrote sits after that watermark, where a subscriber replaying from it
    ///     still sees them.
    /// </summary>
    private async Task<DevWorkflowMutationResult> ExecuteMutationAsync(Guid runId,
        long expectedVersion,
        Guid? operationId,
        Func<DevWorkflowRun, Task<IReadOnlyList<MutationOutcome>>> mutate,
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

            var outcomes = await mutate(run).ConfigureAwait(false);
            var sequence = 0L;
            for (var index = 0; index < outcomes.Count; index++)
            {
                var written = AddEvent(run, outcomes[index].EventType, outcomes[index].NodeRunId, outcomes[index].Outcome, index == 0 ? operationId : null, outcomes[index].DetailJson);
                if (index == 0)
                {
                    sequence = written;
                }

                run.Version++;
            }

            run.UpdatedAtUtc = Now();
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DevWorkflowMutationResult(runId, sequence, run.Version, run.Status, run.GraphRevision, outcomes[0].SupersededArtifactId);
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
    ///     What a node-run seed has to satisfy before any of them is written. Shared by the run start and by dynamic
    ///     expansion, and checked outside the transaction: these are caller mistakes, and letting one reach the unique
    ///     index would surface as a lost race rather than as the argument error it is.
    /// </summary>
    private static void EnsureSeedsValid(IReadOnlyList<DevWorkflowNodeRunSeed> seeds, string parameterName)
    {
        foreach (var seed in seeds)
        {
            EnsureNotBlank(seed.NodeKey, parameterName);
            if (seed.MaxAttempts <= 0)
            {
                throw new ArgumentOutOfRangeException(parameterName, "A node run must allow at least one attempt.");
            }

            // A seed lands a row either waiting to be dispatched or already finished, and nothing between: a live status
            // is a lane's own claim that it holds the row, and creating one with no lane behind it writes a Running row
            // with no start time that nobody is coming back for.
            if (seed.Status != DevWorkflowNodeRunStatus.Pending && !IsTerminal(seed.Status))
            {
                throw new ArgumentException($"Node run seed '{seed.NodeKey}' is seeded {seed.Status}, which is a live status no lane has taken. "
                                            + "A seed lands Pending or terminal.", parameterName);
            }

            // An output document describes what a node run PRODUCED, so a row that has not ended cannot have one: a
            // seed carrying both is a caller saying the row is finished while asking for it to be run.
            if (seed.OutputJson is not null && !IsTerminal(seed.Status))
            {
                throw new ArgumentException($"Node run seed '{seed.NodeKey}' carries an output document but is seeded {seed.Status}. "
                                            + "Only a seed that lands terminal may say what it produced.", parameterName);
            }
        }

        if (seeds.Select(seed => seed.NodeKey).Distinct(StringComparer.Ordinal).Count() != seeds.Count)
        {
            throw new ArgumentException("A materialization cannot create two node runs under the same node key.", parameterName);
        }
    }

    /// <summary>
    ///     Inserts one node run per seed, allocating each row's sequence from the run's own counter. Shared by the run
    ///     start and by dynamic expansion so the two cannot drift on what a fresh node run looks like.
    /// </summary>
    private void AddNodeRuns(DevWorkflowRun run, IReadOnlyList<DevWorkflowNodeRunSeed> seeds, long now)
    {
        foreach (var seed in seeds)
        {
            _dbContext.DevWorkflowNodeRuns.Add(new DevWorkflowNodeRun
            {
                Id = seed.NodeRunId,
                RunId = run.Id,
                NodeKey = seed.NodeKey,
                NodeType = seed.NodeType,
                Attempt = 1,
                MaxAttempts = seed.MaxAttempts,
                SessionResumes = 0,
                Status = seed.Status,
                Sequence = NextSequence(run),
                AgentDefinitionId = seed.AgentDefinitionId,
                DevelopmentProjectId = seed.DevelopmentProjectId,
                InputJson = Utf8OrNull(seed.InputJson),
                OutputJson = Utf8OrNull(seed.OutputJson),
                PolicyResolutionJson = Utf8OrNull(seed.PolicyResolutionJson),
                MaterializedFromNodeRunId = seed.MaterializedFromNodeRunId,
                MaterializationIndex = seed.MaterializationIndex,
                CreatedAtUtc = now,

                // A row seeded terminal never passes through a transition, so the two timestamps a reader reads a
                // duration off are stamped here instead — both at the create, because the work it stands for took no
                // time: it is the record that there was nothing to do.
                StartedAtUtc = IsTerminal(seed.Status) ? now : null,
                EndedAtUtc = IsTerminal(seed.Status) ? now : null
            });
        }
    }

    /// <summary>
    ///     Admits a Retry against the run-wide re-attempt budget, inside the transaction that records it — which is the
    ///     only place the count can be true.
    ///     <para>
    ///         Spent is Σ(Attempt − 1) over the run's node runs: the re-attempts that have actually happened. A Retry
    ///         that is recorded but not yet settled has spent none of that sum and would be invisible to it, so it
    ///         counts as a RESERVATION — the dispatcher turns it into an attempt on a later tick, and until then it is
    ///         an attempt this run has already promised. Settled has one definition here: the node run's <c>Attempt</c>
    ///         has moved past the attempt its decision was recorded against.
    ///     </para>
    ///     <para>
    ///         The decision endpoint checks the same budget first, for the message an operator reads. This is the
    ///         authority: two people answering two blocked node runs in the same tick window both pass a check taken
    ///         before either decision exists, and only a count taken under the writer lock refuses the second. The
    ///         automatic retry path checks it here for the same reason, on the same count, since a human's Retry and a
    ///         policy's re-attempt spend the same budget (FU3-4).
    ///     </para>
    ///     <para>
    ///         <paramref name="cost" /> is how many re-attempts the act being admitted makes. One for a decision or a
    ///         single re-attempt; a routed fix loop costs the whole cascade it resets, because admitting a fan-out one
    ///         attempt at a time is how a run overspends its budget by the width of its graph.
    ///     </para>
    /// </summary>
    private async Task EnsureRetryBudgetAsync(Guid runId, int maxTotalAttempts, int cost, CancellationToken cancellationToken)
    {
        var attempts = await _dbContext.DevWorkflowNodeRuns.AsNoTracking()
                                       .Where(entity => entity.RunId == runId)
                                       .Select(entity => new NodeRunAttempt(entity.Id, entity.Attempt))
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);
        var recorded = await _dbContext.DevWorkflowDecisions.AsNoTracking()
                                       .Where(entity => entity.RunId == runId && entity.Decision == DevWorkflowDecisionKind.Retry)
                                       .Select(entity => new NodeRunAttempt(entity.NodeRunId, entity.Attempt))
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);

        var spent = attempts.Sum(static row => row.Attempt - 1);
        var reserved = recorded.Count(decision => attempts.Any(row => row.NodeRunId == decision.NodeRunId && row.Attempt == decision.Attempt));
        // Strictly greater, with the cost of THIS act included: at cost 1 that is algebraically the `spent + reserved
        // >= maxTotalAttempts` this always was, so admitting a decision is unchanged.
        if (spent + reserved + cost > maxTotalAttempts)
        {
            throw new DevWorkflowRetryBudgetExceededException($"This run has already spent or promised {spent + reserved} re-attempts, which is as many "
                                                              + "re-attempts as this run allows, so it cannot be retried again.");
        }
    }

    /// <summary>Projected to the one column the caller compares, so a replay probe never decrypts an event's detail.</summary>
    public async Task<string?> FindOperationEventTypeAsync(Guid runId, Guid operationId, CancellationToken cancellationToken = default) =>
        await _dbContext.DevWorkflowRunEvents.AsNoTracking()
                        .Where(entity => entity.RunId == runId && entity.OperationId == operationId)
                        .Select(entity => entity.EventType)
                        .SingleOrDefaultAsync(cancellationToken)
                        .ConfigureAwait(false);

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

        // The SAME options the write uses, and the reason is the read rather than the write: the Web defaults are
        // case-INSENSITIVE, so this binds a row written before FX-D (PascalCase) and one written after (camelCase)
        // alike. The log is append-only — a case-sensitive read here would answer null for every artifact superseded
        // before the casing was fixed, and a replay answering null skips a blob sweep it still owes.
        return JsonSerializer.Deserialize<ArtifactSupersessionDetail>(recorded.DetailJson, JsonOptions)?.SupersededArtifactId;
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

    /// <summary>
    ///     SHA-256 of a payload's bytes, lowercase hex, computed here — beside the blob it describes — so a hash and the
    ///     text it names can never drift apart. Used for a definition's graph and a rule set's body alike.
    /// </summary>
    private static string HashPayload(byte[] payload) =>
        Convert.ToHexStringLower(SHA256.HashData(payload));

    private static byte[]? ReasonDetail(string? sanitizedReason) =>
        string.IsNullOrWhiteSpace(sanitizedReason) ? null : Utf8(JsonSerializer.Serialize(new ReasonDetailPayload(sanitizedReason), JsonOptions));

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

    /// <summary>A node run and the attempt number some row is stamped with — the two columns the budget count needs.</summary>
    private sealed record NodeRunAttempt(Guid NodeRunId, int Attempt);

    private sealed record ReasonDetailPayload(string Reason);

    private sealed record MaterializationDetail(int NodeRunCount, int GraphRevision);

    private sealed record ArtifactSupersessionDetail(Guid SupersededArtifactId, string SupersededManagedReference, int Version);

    private sealed record ArtifactUseDetail(int ArtifactCount);

    private sealed record StaleMarkDetail(Guid SupersededArtifactId, Guid SupersedingArtifactId, int MarkedCount);

    private sealed record WorkSessionAttachedDetail(Guid WorkSessionId, int Attempt, int SessionResumes);
}
