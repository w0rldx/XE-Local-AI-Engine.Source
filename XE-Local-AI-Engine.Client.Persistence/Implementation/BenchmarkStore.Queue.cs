namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class BenchmarkStore
{
    public Task<int> CountRunsAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        _dbContext.BenchmarkRuns.AsNoTracking().CountAsync(entity => entity.ProjectId == projectId, cancellationToken);

    public async Task<IReadOnlyDictionary<BenchmarkWorkKind, int>> CountActiveWorkAsync(Guid projectId,
        CancellationToken cancellationToken = default) =>
        (await _dbContext.BenchmarkWorkItems.AsNoTracking()
                         .Where(item => (item.Status == BenchmarkWorkStatus.Queued || item.Status == BenchmarkWorkStatus.Running)
                                        && _dbContext.BenchmarkRuns.Any(run => run.Id == item.RunId && run.ProjectId == projectId))
                         .GroupBy(item => item.Kind)
                         .Select(group => new
                         {
                             Kind = group.Key,
                             Count = group.Count()
                         })
                         .ToListAsync(cancellationToken)
                         .ConfigureAwait(false))
        .ToDictionary(static entry => entry.Kind, static entry => entry.Count);

    public async Task<BenchmarkClaimedWork?> ClaimNextAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var candidate = await _dbContext.BenchmarkWorkItems.AsNoTracking()
                                            .Where(entity => entity.Status == BenchmarkWorkStatus.Queued)
                                            .OrderBy(entity => entity.QueueSequence)
                                            .Select(entity => new
                                            {
                                                entity.QueueSequence,
                                                entity.Version
                                            })
                                            .FirstOrDefaultAsync(cancellationToken)
                                            .ConfigureAwait(false);
            if (candidate is null)
            {
                return null;
            }

            var now = Now();
            var nextVersion = candidate.Version + 1;
            var claimed = await _dbContext.BenchmarkWorkItems
                                          .Where(entity => entity.QueueSequence == candidate.QueueSequence
                                                           && entity.Version == candidate.Version
                                                           && entity.Status == BenchmarkWorkStatus.Queued)
                                          .ExecuteUpdateAsync(setters => setters
                                                                         .SetProperty(entity => entity.Status, BenchmarkWorkStatus.Running)
                                                                         .SetProperty(entity => entity.StartedAtUtc, now)
                                                                         .SetProperty(entity => entity.Version, nextVersion), cancellationToken)
                                          .ConfigureAwait(false);
            if (claimed == 0)
            {
                continue;
            }

            // ExecuteUpdate bypasses the change tracker. A scoped store may still be tracking the just-enqueued work
            // item at its previous version, so reload from the database before the lifecycle transition is composed.
            _dbContext.ChangeTracker.Clear();
            var work = await _dbContext.BenchmarkWorkItems.AsNoTracking().SingleAsync(entity => entity.QueueSequence == candidate.QueueSequence, cancellationToken).ConfigureAwait(false);
            var run = await RequireRunAsync(work.RunId, tracking: true, cancellationToken).ConfigureAwait(false);
            // One explicit arm per kind. A bare `else` here would send a Fidelity or Comparison item down the judge
            // path, where it would dereference a null JudgeAttemptId, throw InvalidJudgeTransition and stall the
            // single-consumer queue behind an item it can never claim.
            switch (work.Kind)
            {
                case BenchmarkWorkKind.Primary:
                    if (run.PrimaryStatus != BenchmarkPrimaryStatus.Queued)
                    {
                        throw new BenchmarkConflictException("InvalidPrimaryTransition");
                    }

                    run.PrimaryStatus = BenchmarkPrimaryStatus.Running;
                    run.StartedAtUtc = now;
                    break;
                case BenchmarkWorkKind.Judge:
                    {
                        // The judging's whole lifecycle lives on its attempt; the run only bumps its version so a reader
                        // polling the run still sees that something about it changed.
                        var attempt = await RequireJudgeAttemptAsync(work.JudgeAttemptId ?? throw new BenchmarkConflictException("InvalidJudgeTransition"),
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (attempt.Status != BenchmarkJudgeAttemptStatus.Queued)
                        {
                            throw new BenchmarkConflictException("InvalidJudgeTransition");
                        }

                        attempt.Status = BenchmarkJudgeAttemptStatus.Running;
                        attempt.StartedAtUtc = now;
                        attempt.Version++;
                        break;
                    }

                case BenchmarkWorkKind.Fidelity:
                    {
                        var attempt = await RequireFidelityAttemptAsync(work.FidelityAttemptId ?? throw new BenchmarkConflictException("InvalidFidelityTransition"),
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (attempt.Status != BenchmarkJudgeAttemptStatus.Queued)
                        {
                            throw new BenchmarkConflictException("InvalidFidelityTransition");
                        }

                        attempt.Status = BenchmarkJudgeAttemptStatus.Running;
                        attempt.StartedAtUtc = now;
                        attempt.Version++;

                        // The projection follows the attempt through every transition, not only the terminal ones. Left
                        // reading 'queued' for the hours a measurement actually takes, it says nothing has started.
                        run.FidelityStatus = ToFidelityStatus(BenchmarkJudgeAttemptStatus.Running);
                        break;
                    }

                case BenchmarkWorkKind.Comparison:
                    {
                        var comparison = await RequireComparisonAsync(work.ComparisonId ?? throw new BenchmarkConflictException("InvalidComparisonTransition"),
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (comparison.Status != BenchmarkJudgeAttemptStatus.Queued)
                        {
                            throw new BenchmarkConflictException("InvalidComparisonTransition");
                        }

                        comparison.Status = BenchmarkJudgeAttemptStatus.Running;
                        comparison.StartedAtUtc = now;
                        comparison.Version++;
                        break;
                    }

                default:
                    throw new BenchmarkConflictException("UnknownWorkKind");
            }

            // Every per-run kind bumps the run's version, so a reader polling the run sees that something about it
            // changed. A comparison is NOT an event in a run's life: it names two runs and its work item names only
            // the canonical first, so bumping that one invalidated its CAS token on every pairwise claim — scoring,
            // deleting or re-measuring it returned VersionConflict throughout a tournament, and the other run of the
            // pair never heard about it anyway. The fit's own publication is what refreshes a pairwise reader.
            if (work.Kind != BenchmarkWorkKind.Comparison)
            {
                run.Version++;
                run.UpdatedAtUtc = now;
            }

            await SaveAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new BenchmarkClaimedWork(work.QueueSequence,
                work.RunId,
                work.Kind,
                work.Attempt,
                work.Version,
                ToRecord(run),
                work.JudgeAttemptId,
                work.FidelityAttemptId,
                work.ComparisonId);
        }
    }

    public async Task<BenchmarkRunRecord> MarkPrimarySucceededAsync(BenchmarkPrimarySuccessCommand command, CancellationToken cancellationToken = default)
    {
        if (command.OutputPartsJson.IsEmpty)
        {
            throw new BenchmarkValidationException("Successful primary output cannot be empty.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireWorkCompletionAsync(command.RunId, BenchmarkWorkKind.Primary, command.ExpectedWorkVersion, cancellationToken).ConfigureAwait(false);
        _dbContext.ChangeTracker.Clear();
        var run = await RequireRunAsync(command.RunId, tracking: true, cancellationToken).ConfigureAwait(false);
        if (run.PrimaryStatus == BenchmarkPrimaryStatus.Succeeded)
        {
            return await ToRecordWithJudgeAsync(run, cancellationToken).ConfigureAwait(false);
        }

        var work = await RequireWorkAsync(run.Id, BenchmarkWorkKind.Primary, cancellationToken).ConfigureAwait(false);
        EnsureVersion(work.Version, command.ExpectedWorkVersion);
        if (run.PrimaryStatus == BenchmarkPrimaryStatus.CancelRequested)
        {
            var cancelledAt = Now();
            run.PrimaryStatus = BenchmarkPrimaryStatus.Cancelled;
            run.PrimaryErrorMessage = null;
            run.OutputPartsJson = null;
            run.LastStreamSequence = 0;
            run.EffectiveContextTokens = null;
            run.DurationMs = null;
            run.TotalTokens = null;
            run.TokensPerSecond = null;
            run.PrimaryStopReason = null;
            ApplyThroughput(run, throughput: null);
            run.PrimaryCompletedAtUtc = cancelledAt;
            run.Version++;
            run.UpdatedAtUtc = cancelledAt;
            TerminalizeWork(work, BenchmarkWorkStatus.Cancelled, errorMessage: null, cancelledAt);
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return await ToRecordWithJudgeAsync(run, cancellationToken).ConfigureAwait(false);
        }

        EnsurePrimaryState(run, BenchmarkPrimaryStatus.Running);
        var now = Now();
        run.PrimaryStatus = BenchmarkPrimaryStatus.Succeeded;
        run.OutputPartsJson = command.OutputPartsJson.ToArray();
        run.LastStreamSequence = command.LastStreamSequence;
        run.EffectiveContextTokens = command.EffectiveContextTokens;
        run.DurationMs = command.DurationMs;
        run.TotalTokens = command.TotalTokens;
        run.TokensPerSecond = command.TokensPerSecond;
        run.PrimaryStopReason = command.PrimaryStopReason;
        ApplyThroughput(run, command.Throughput);
        run.PrimaryCompletedAtUtc = now;
        run.Version++;
        run.UpdatedAtUtc = now;
        TerminalizeWork(work, BenchmarkWorkStatus.Succeeded, errorMessage: null, now);
        // Flat columns only: deciding whether to judge or to measure must not decrypt the project's core task. No
        // revision pointer means nothing to judge under, and the run simply never gets an attempt.
        var settings = await _dbContext.BenchmarkProjects.AsNoTracking()
                                       .Where(entity => entity.Id == run.ProjectId)
                                       .Select(entity => new
                                       {
                                           entity.CurrentJudgePolicyRevisionId,
                                           entity.FidelityEnabled,
                                           entity.FidelityKldEnabled
                                       })
                                       .SingleOrDefaultAsync(cancellationToken)
                                       .ConfigureAwait(false);
        if (settings?.CurrentJudgePolicyRevisionId is { } revisionId)
        {
            var seed = command.JudgeAttempt;

            // The runtime was resolved for one specific revision. If the project moved on meanwhile, abandoning the
            // transaction rolls primary success back with it, and the caller re-resolves and retries.
            if (seed?.ExpectedJudgePolicyRevisionId is { } expectedRevisionId && expectedRevisionId != revisionId)
            {
                throw new BenchmarkJudgePolicyChangedException("The project's judge policy changed while the run was executing.");
            }

            var revision = await RequireJudgePolicyRevisionAsync(revisionId, cancellationToken).ConfigureAwait(false);
            _ = await InsertJudgeAttemptAsync(run,
                    revision,
                    seed?.RuntimeJson,
                    seed?.RuntimeUnresolvedReason,
                    seed?.LaunchIntent,
                    now,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // Seeded here rather than at freeze, for the judge attempt's own reason: a measurement is queued against an
        // answer, so it must not exist until there IS one. The eligibility rule is the per-cell one shared with the
        // freeze marker and with EnqueueMissingFidelityAsync — the current project settings decide it, because the
        // fidelity settings deliberately write through the freeze.
        if (settings is { FidelityEnabled: true } && await IsFidelityMeasuredCellAsync(run, cancellationToken).ConfigureAwait(false))
        {
            _ = await AppendFidelityWorkAsync(run, settings.FidelityKldEnabled ? FidelityKindKld : FidelityKindPerplexity, now, cancellationToken)
                .ConfigureAwait(false);
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ToRecordWithJudgeAsync(run, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     The repeat half of the measured-cell rule: non-warm-up, first of its group.
    /// </summary>
    private static bool IsFidelityMeasuredRepeat(BenchmarkRun run) =>
        !run.IsWarmup && run.RepeatIndex is null or 1;

    /// <summary>
    ///     The one run of a cell that a fidelity measurement is attached to: the repeat half above, plus the
    ///     lowest-indexed task item of the cell. One rule, three callers — freeze's "skipped" marker, the seed on
    ///     primary success, and the measure-existing sweep — because a cell measured by one of them and a cell measured
    ///     by another must mean the same thing. Freeze decides it over its own batch (those rows are not saved yet) and
    ///     <c>EnqueueMissingFidelityAsync</c> re-expresses it as an EF predicate; all three must change together.
    ///     <para>
    ///         The item half exists because perplexity and KL divergence measure the model file against a corpus, not
    ///         the task: every item of one cell would otherwise queue an identical measurement at N times the cost.
    ///         A pre-suite run carries a null <c>TaskItemIndex</c>, which no sibling can undercut, so nothing about it
    ///         changes.
    ///     </para>
    /// </summary>
    private async Task<bool> IsFidelityMeasuredCellAsync(BenchmarkRun run, CancellationToken cancellationToken) =>
        IsFidelityMeasuredRepeat(run)
        && !await _dbContext.BenchmarkRuns.AsNoTracking()
                            .AnyAsync(other => other.ProjectId == run.ProjectId
                                               && other.CellKey == run.CellKey
                                               && other.TaskItemIndex < run.TaskItemIndex,
                                cancellationToken)
                            .ConfigureAwait(false);

    /// <summary>
    ///     The cell key of a run that is a cell of one. Derived from the run's own id, so it is unique by construction
    ///     rather than by hoping the column was populated, and two freezes of one project never share a cell.
    /// </summary>
    internal static string SingletonCellKey(Guid runId) =>
        "cell:" + runId.ToString("D");

    public Task<BenchmarkRunRecord> MarkPrimaryFailedAsync(Guid runId, long expectedRunVersion, string errorMessage, CancellationToken cancellationToken = default) =>
        TerminalizePrimaryNonSuccessAsync(runId, expectedRunVersion, BenchmarkPrimaryStatus.Failed, BenchmarkWorkStatus.Failed, errorMessage,
            lastStreamSequence: 0, primaryStopReason: null, cancellationToken);

    public Task<BenchmarkRunRecord> MarkPrimaryFailedAsync(Guid runId,
        long expectedRunVersion,
        string errorMessage,
        long lastStreamSequence,
        string? primaryStopReason = null,
        CancellationToken cancellationToken = default) =>
        TerminalizePrimaryNonSuccessAsync(runId, expectedRunVersion, BenchmarkPrimaryStatus.Failed, BenchmarkWorkStatus.Failed, errorMessage,
            lastStreamSequence, primaryStopReason, cancellationToken);

    public Task<BenchmarkRunRecord> MarkPrimaryCancelledAsync(Guid runId, long expectedRunVersion, CancellationToken cancellationToken = default) =>
        TerminalizePrimaryNonSuccessAsync(runId, expectedRunVersion, BenchmarkPrimaryStatus.Cancelled, BenchmarkWorkStatus.Cancelled, string.Empty,
            lastStreamSequence: 0, primaryStopReason: null, cancellationToken);

    public Task<BenchmarkRunRecord> MarkPrimaryCancelledAsync(Guid runId,
        long expectedRunVersion,
        long lastStreamSequence,
        CancellationToken cancellationToken = default) =>
        TerminalizePrimaryNonSuccessAsync(runId, expectedRunVersion, BenchmarkPrimaryStatus.Cancelled, BenchmarkWorkStatus.Cancelled, string.Empty,
            lastStreamSequence, primaryStopReason: null, cancellationToken);

    public async Task<BenchmarkRunRecord> MarkJudgeSucceededAsync(BenchmarkJudgeSuccessCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.JudgeResultJson.IsEmpty)
        {
            throw new BenchmarkValidationException("Successful judge output cannot be empty.");
        }

        return await TerminalizeJudgeAsync(command.RunId,
                command.ExpectedWorkVersion,
                BenchmarkJudgeAttemptStatus.Succeeded,
                BenchmarkWorkStatus.Succeeded,
                errorMessage: null,
                command.LastStreamSequence,
                (attempt, promote) =>
                {
                    attempt.ResultJson = command.JudgeResultJson.ToArray();
                    attempt.Score = command.Score;

                    // A judging with no spawn never reached MarkJudgeLaunchReadyAsync, so its key is set here. NULL
                    // stays the only thing this can fill: a measured identity is written once, at launch, and an
                    // incomplete one must never be repaired into a rankable one afterwards.
                    attempt.JudgeExecutionKey ??= command.VerifiedExecutionKey;
                    return promote;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<BenchmarkRunRecord> MarkJudgeFailedAsync(Guid runId,
        long expectedRunVersion,
        string errorMessage,
        CancellationToken cancellationToken = default) =>
        MarkJudgeFailedAsync(runId, expectedRunVersion, errorMessage, lastStreamSequence: 0, cancellationToken);

    public Task<BenchmarkRunRecord> MarkJudgeFailedAsync(Guid runId,
        long expectedRunVersion,
        string errorMessage,
        long lastStreamSequence,
        CancellationToken cancellationToken = default) =>
        TerminalizeJudgeAsync(runId,
            expectedRunVersion,
            BenchmarkJudgeAttemptStatus.Failed,
            BenchmarkWorkStatus.Failed,
            Sanitize(errorMessage),
            lastStreamSequence,
            static (_, _) => false,
            cancellationToken);

    public Task<BenchmarkRunRecord> MarkJudgeCancelledAsync(Guid runId,
        long expectedRunVersion,
        CancellationToken cancellationToken = default) =>
        MarkJudgeCancelledAsync(runId, expectedRunVersion, lastStreamSequence: 0, cancellationToken);

    public Task<BenchmarkRunRecord> MarkJudgeCancelledAsync(Guid runId,
        long expectedRunVersion,
        long lastStreamSequence,
        CancellationToken cancellationToken = default) =>
        TerminalizeJudgeAsync(runId,
            expectedRunVersion,
            BenchmarkJudgeAttemptStatus.Cancelled,
            BenchmarkWorkStatus.Cancelled,
            errorMessage: null,
            lastStreamSequence,
            static (_, _) => false,
            cancellationToken);

    /// <summary>
    ///     The one judge terminalization path. The compare-and-swap is on the immutable work item, the state lives on
    ///     the attempt, and the run only moves its version and stream sequence — a judging is not run state.
    /// </summary>
    /// <param name="apply">
    ///     Writes the terminal payload onto the attempt and returns whether this outcome may claim the rank cohort.
    ///     Only a success may: a failed or cancelled judging must never define what the ranked runs are compared to.
    /// </param>
    private async Task<BenchmarkRunRecord> TerminalizeJudgeAsync(Guid runId,
        long expectedWorkVersion,
        BenchmarkJudgeAttemptStatus attemptStatus,
        BenchmarkWorkStatus workStatus,
        string? errorMessage,
        long lastStreamSequence,
        Func<BenchmarkJudgeAttempt, bool, bool> apply,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireWorkCompletionAsync(runId, BenchmarkWorkKind.Judge, expectedWorkVersion, cancellationToken).ConfigureAwait(false);
        _dbContext.ChangeTracker.Clear();
        var run = await RequireRunAsync(runId, tracking: true, cancellationToken).ConfigureAwait(false);
        var work = await RequireWorkAsync(run.Id, BenchmarkWorkKind.Judge, cancellationToken).ConfigureAwait(false);
        EnsureVersion(work.Version, expectedWorkVersion);
        var now = Now();
        TerminalizeWork(work, workStatus, errorMessage, now);
        var attempt = await TerminalizeJudgeAttemptAsync(work, attemptStatus, errorMessage, now, cancellationToken).ConfigureAwait(false);
        if (attempt is null)
        {
            // Already terminal: repeating a terminalization must not write a second result or bump anything.
            return await ToRecordWithJudgeAsync(run, cancellationToken).ConfigureAwait(false);
        }

        if (apply(attempt, attemptStatus == BenchmarkJudgeAttemptStatus.Succeeded) && attempt.JudgeExecutionKey is { } executionKey)
        {
            // The cohort is claimed by the first SUCCESS of the live generation, never at readiness: a failed first
            // attempt must not poison the ranking for the runtime it happened to run on.
            _ = await TryPromoteReferenceExecutionKeyAsync(attempt.PolicyRevisionId, attempt.CohortGeneration, executionKey, cancellationToken)
                .ConfigureAwait(false);
        }

        UpdateLastStreamSequence(run, lastStreamSequence);
        run.Version++;
        run.UpdatedAtUtc = now;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ToRecordWithJudgeAsync(run, cancellationToken).ConfigureAwait(false);
    }
}
