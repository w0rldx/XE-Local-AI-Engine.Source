namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class BenchmarkStore
{
    public async Task<bool> MarkPrimaryLaunchReadyAsync(Guid runId,
        long workItemId,
        long claimedWorkVersion,
        BenchmarkLaunchReceiptCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var work = await _dbContext.BenchmarkWorkItems.AsNoTracking()
                                   .SingleOrDefaultAsync(entity => entity.QueueSequence == workItemId, cancellationToken)
                                   .ConfigureAwait(false);

        // Running at the claimed version is the ordinary case; Cancelled at its successor is the proven cancel-first
        // ordering, where the work item was terminalized (Version + 1) while this launch was still coming up.
        if (work is null
            || work.RunId != runId
            || work.Kind != BenchmarkWorkKind.Primary
            || !((work.Status == BenchmarkWorkStatus.Running && work.Version == claimedWorkVersion)
                 || (work.Status == BenchmarkWorkStatus.Cancelled && work.Version == claimedWorkVersion + 1)))
        {
            return false;
        }

        var run = await RequireRunAsync(runId, tracking: true, cancellationToken).ConfigureAwait(false);
        if (run.PrimaryLaunchReceiptJson is not null || run.PrimaryEnvironmentFactsJson is not null)
        {
            return false;
        }

        run.PrimaryLaunchReceiptJson = command.ReceiptJson is null ? null : Encoding.UTF8.GetBytes(command.ReceiptJson);
        run.PrimaryEnvironmentFactsJson = Encoding.UTF8.GetBytes(command.EnvironmentFactsJson);
        run.PrimaryReceiptHash = command.ReceiptHash;
        run.PrimaryEnvironmentFactsHash = command.EnvironmentFactsHash;
        run.PrimaryEffectiveLaunchIdentity = command.EffectiveLaunchIdentity;
        run.PrimaryEffectiveBackend = command.EffectiveBackend;
        run.PrimaryPlacementOffloaded = command.PlacementOffloaded;
        run.PrimaryPlacementTotal = command.PlacementTotal;
        run.PrimaryLaunchExecutableSha256 = command.ExecutableSha256;
        run.PrimaryLaunchHasAuxAssets = command.HasAuxAssets;
        run.PrimaryLaunchKvCacheTypeSource = command.KvCacheTypeSource;

        // The run's own version is deliberately left alone: the checkpoint is evidence, not a lifecycle transition,
        // and bumping it would 409 an operator cancellation that is holding the version it just read.
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> MarkJudgeLaunchReadyAsync(Guid attemptId,
        long workItemId,
        long claimedWorkVersion,
        BenchmarkLaunchReceiptCommand command,
        string? judgeExecutionKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var work = await _dbContext.BenchmarkWorkItems.AsNoTracking()
                                   .SingleOrDefaultAsync(entity => entity.QueueSequence == workItemId, cancellationToken)
                                   .ConfigureAwait(false);

        // Same CAS as the primary: Running at the claimed version, or Cancelled at its successor — the proven
        // cancel-first ordering, where the work item was terminalized while this launch was still coming up.
        if (work is null
            || work.Kind != BenchmarkWorkKind.Judge
            || work.JudgeAttemptId != attemptId
            || !((work.Status == BenchmarkWorkStatus.Running && work.Version == claimedWorkVersion)
                 || (work.Status == BenchmarkWorkStatus.Cancelled && work.Version == claimedWorkVersion + 1)))
        {
            return false;
        }

        var attempt = await RequireJudgeAttemptAsync(attemptId, cancellationToken).ConfigureAwait(false);
        if (attempt.LaunchReceiptJson is not null || attempt.EnvironmentFactsJson is not null)
        {
            return false;
        }

        attempt.LaunchReceiptJson = command.ReceiptJson is null ? null : Encoding.UTF8.GetBytes(command.ReceiptJson);
        attempt.EnvironmentFactsJson = Encoding.UTF8.GetBytes(command.EnvironmentFactsJson);
        attempt.ReceiptHash = command.ReceiptHash;
        attempt.EnvironmentFactsHash = command.EnvironmentFactsHash;
        attempt.EffectiveLaunchIdentity = command.EffectiveLaunchIdentity;
        attempt.EffectiveBackend = command.EffectiveBackend;
        attempt.PlacementOffloaded = command.PlacementOffloaded;
        attempt.PlacementTotal = command.PlacementTotal;
        attempt.LaunchExecutableSha256 = command.ExecutableSha256;
        attempt.LaunchHasAuxAssets = command.HasAuxAssets;
        attempt.LaunchKvCacheTypeSource = command.KvCacheTypeSource;

        // Written here and only here: the cohort key describes what this attempt actually launched. NULL stays NULL —
        // an incomplete execution identity must never be repaired into a rankable one later.
        attempt.JudgeExecutionKey = judgeExecutionKey;

        // The attempt's own version is deliberately left alone: the checkpoint is evidence, not a lifecycle
        // transition, and bumping it would invalidate the completion token the executor is holding.
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<BenchmarkRunRecord> CancelAsync(Guid runId, long expectedRunVersion, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var run = await RequireRunAsync(runId, tracking: true, cancellationToken).ConfigureAwait(false);
        if (run.PrimaryStatus is BenchmarkPrimaryStatus.CancelRequested or BenchmarkPrimaryStatus.Cancelled)
        {
            return await ToRecordWithJudgeAsync(run, cancellationToken).ConfigureAwait(false);
        }

        var currentAttempt = run.CurrentJudgeAttemptId is { } currentAttemptId
            ? await RequireJudgeAttemptAsync(currentAttemptId, cancellationToken).ConfigureAwait(false)
            : null;
        if (run.PrimaryStatus == BenchmarkPrimaryStatus.Succeeded
            && currentAttempt?.Status == BenchmarkJudgeAttemptStatus.Cancelled)
        {
            return await ToRecordWithJudgeAsync(run, cancellationToken).ConfigureAwait(false);
        }

        EnsureVersion(run.Version, expectedRunVersion);
        var now = Now();
        if (run.PrimaryStatus == BenchmarkPrimaryStatus.Queued)
        {
            run.PrimaryStatus = BenchmarkPrimaryStatus.Cancelled;
            run.PrimaryCompletedAtUtc = now;
            var work = await RequireWorkAsync(run.Id, BenchmarkWorkKind.Primary, cancellationToken).ConfigureAwait(false);
            TerminalizeWork(work, BenchmarkWorkStatus.Cancelled, errorMessage: null, now);
        }
        else if (run.PrimaryStatus == BenchmarkPrimaryStatus.Running)
        {
            run.PrimaryStatus = BenchmarkPrimaryStatus.CancelRequested;
        }
        else if (run.PrimaryStatus == BenchmarkPrimaryStatus.Succeeded
                 && currentAttempt?.Status is BenchmarkJudgeAttemptStatus.Queued or BenchmarkJudgeAttemptStatus.Running)
        {
            var work = await RequireWorkAsync(run.Id, BenchmarkWorkKind.Judge, cancellationToken).ConfigureAwait(false);
            TerminalizeWork(work, BenchmarkWorkStatus.Cancelled, errorMessage: null, now);
            _ = await TerminalizeJudgeAttemptAsync(work, BenchmarkJudgeAttemptStatus.Cancelled, errorMessage: null, now, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            throw new BenchmarkConflictException("InvalidCancellationTransition");
        }

        run.Version++;
        run.UpdatedAtUtc = now;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return await ToRecordWithJudgeAsync(run, cancellationToken).ConfigureAwait(false);
    }

    public async Task<BenchmarkRunRecord> SetUserScoreAsync(Guid runId,
        int? score,
        long expectedRunVersion,
        CancellationToken cancellationToken = default)
    {
        if (score is < 0 or > 100)
        {
            throw new BenchmarkValidationException("Score must be between 0 and 100.");
        }

        var run = await RequireRunAsync(runId, tracking: true, cancellationToken).ConfigureAwait(false);
        EnsureVersion(run.Version, expectedRunVersion);
        if (run.PrimaryStatus != BenchmarkPrimaryStatus.Succeeded)
        {
            throw new BenchmarkConflictException("PrimaryNotSucceeded");
        }

        run.UserScore = score;
        run.Version++;
        run.UpdatedAtUtc = Now();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return await ToRecordWithJudgeAsync(run, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> RecoverOnStartupAsync(CancellationToken cancellationToken = default) =>
        (await RecoverRunsOnStartupAsync(cancellationToken).ConfigureAwait(false)).Count;

    public async Task<IReadOnlyList<BenchmarkRunRecord>> RecoverRunsOnStartupAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var activeWork = await _dbContext.BenchmarkWorkItems.Where(entity => entity.Status == BenchmarkWorkStatus.Running).ToListAsync(cancellationToken).ConfigureAwait(false);
        var cancelRequested = await _dbContext.BenchmarkRuns.Where(entity => entity.PrimaryStatus == BenchmarkPrimaryStatus.CancelRequested).ToListAsync(cancellationToken).ConfigureAwait(false);
        var recoveredRunIds = new HashSet<Guid>();
        var now = Now();

        // Attempts first, so the work-item pass below finds them already terminal. A result is never overwritten:
        // only an attempt still marked Running — i.e. one whose judging died with the process — is touched.
        var interruptedAttempts = await _dbContext.BenchmarkJudgeAttempts
                                                  .Where(entity => entity.Status == BenchmarkJudgeAttemptStatus.Running)
                                                  .ToListAsync(cancellationToken)
                                                  .ConfigureAwait(false);
        foreach (var attempt in interruptedAttempts)
        {
            attempt.Status = BenchmarkJudgeAttemptStatus.Failed;
            attempt.ErrorMessage = InterruptedMessage;
            attempt.CompletedAtUtc = now;
            attempt.Version++;
        }

        // Two sibling sweeps, for the same reason: a fidelity attempt or a comparison left Running by a killed
        // process whose work item a previous partial recovery already terminalized would otherwise stay Running
        // forever, with nothing left to reach it.
        var interruptedFidelity = await _dbContext.BenchmarkFidelityAttempts
                                                  .Where(entity => entity.Status == BenchmarkJudgeAttemptStatus.Running)
                                                  .ToListAsync(cancellationToken)
                                                  .ConfigureAwait(false);
        foreach (var attempt in interruptedFidelity)
        {
            attempt.Status = BenchmarkJudgeAttemptStatus.Failed;
            attempt.ErrorMessage = InterruptedMessage;
            attempt.CompletedAtUtc = now;
            attempt.Version++;

            // The run's fidelity NUMBERS are deliberately untouched — the last attempt that actually succeeded still
            // stands. Its STATUS is not: left reading 'queued'/'running' with no attempt and no work item behind it,
            // every API reports an active measurement forever, the poller never stops and the UI keeps re-measure
            // disabled on a run nothing is measuring.
            var owner = await _dbContext.BenchmarkRuns.SingleAsync(entity => entity.Id == attempt.RunId, cancellationToken).ConfigureAwait(false);
            owner.FidelityStatus = ToFidelityStatus(BenchmarkJudgeAttemptStatus.Failed);
            owner.FidelityErrorMessage = InterruptedMessage;
            owner.Version++;
            owner.LastStreamSequence = checked(owner.LastStreamSequence + 1);
            owner.UpdatedAtUtc = now;
            _ = recoveredRunIds.Add(owner.Id);
        }

        var interruptedComparisons = await _dbContext.BenchmarkComparisons
                                                     .Where(entity => entity.Status == BenchmarkJudgeAttemptStatus.Running)
                                                     .ToListAsync(cancellationToken)
                                                     .ConfigureAwait(false);
        foreach (var comparison in interruptedComparisons)
        {
            comparison.Status = BenchmarkJudgeAttemptStatus.Failed;
            comparison.ErrorMessage = InterruptedMessage;
            comparison.CompletedAtUtc = now;
            comparison.Version++;
        }

        foreach (var work in activeWork)
        {
            var run = await _dbContext.BenchmarkRuns.SingleAsync(entity => entity.Id == work.RunId, cancellationToken).ConfigureAwait(false);
            var cancelledPrimary = work.Kind == BenchmarkWorkKind.Primary
                                   && run.PrimaryStatus == BenchmarkPrimaryStatus.CancelRequested;
            work.Status = cancelledPrimary ? BenchmarkWorkStatus.Cancelled : BenchmarkWorkStatus.Failed;
            work.ErrorMessage = cancelledPrimary ? null : InterruptedMessage;
            work.FinishedAtUtc = now;
            work.Version++;
            switch (work.Kind)
            {
                case BenchmarkWorkKind.Primary:
                    run.PrimaryStatus = cancelledPrimary ? BenchmarkPrimaryStatus.Cancelled : BenchmarkPrimaryStatus.Failed;
                    run.PrimaryErrorMessage = cancelledPrimary ? null : InterruptedMessage;
                    run.PrimaryCompletedAtUtc = now;
                    break;
                case BenchmarkWorkKind.Judge:
                    _ = await TerminalizeJudgeAttemptAsync(work, BenchmarkJudgeAttemptStatus.Failed, InterruptedMessage, now, cancellationToken).ConfigureAwait(false);
                    break;
                case BenchmarkWorkKind.Fidelity:
                    _ = await TerminalizeFidelityAttemptAsync(work.FidelityAttemptId, BenchmarkJudgeAttemptStatus.Failed, InterruptedMessage, now, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                case BenchmarkWorkKind.Comparison:
                    _ = await TerminalizeComparisonAsync(work.ComparisonId, BenchmarkJudgeAttemptStatus.Failed, InterruptedMessage, now, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                default:
                    throw new BenchmarkConflictException("UnknownWorkKind");
            }

            run.Version++;
            run.LastStreamSequence = checked(run.LastStreamSequence + 1);
            run.UpdatedAtUtc = now;
            recoveredRunIds.Add(run.Id);
        }

        foreach (var run in cancelRequested.Where(run => activeWork.All(work => work.RunId != run.Id)))
        {
            run.PrimaryStatus = BenchmarkPrimaryStatus.Cancelled;
            run.PrimaryCompletedAtUtc = now;
            run.Version++;
            run.LastStreamSequence = checked(run.LastStreamSequence + 1);
            run.UpdatedAtUtc = now;
            recoveredRunIds.Add(run.Id);
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return recoveredRunIds
               .Select(id => ToRecord(_dbContext.BenchmarkRuns.Local.Single(run => run.Id == id)))
               .ToArray();
    }

    public async Task DeleteRunAsync(Guid runId, long expectedRunVersion, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var run = await RequireRunAsync(runId, tracking: true, cancellationToken).ConfigureAwait(false);
        EnsureVersion(run.Version, expectedRunVersion);
        if (!IsPrimaryTerminal(run.PrimaryStatus)
            || await _dbContext.BenchmarkWorkItems.AnyAsync(entity => entity.RunId == runId
                                                                      && (entity.Status == BenchmarkWorkStatus.Queued || entity.Status == BenchmarkWorkStatus.Running), cancellationToken)
                               .ConfigureAwait(false)
            || await _dbContext.BenchmarkJudgeAttempts.AnyAsync(entity => entity.RunId == runId
                                                                          && (entity.Status == BenchmarkJudgeAttemptStatus.Queued
                                                                              || entity.Status == BenchmarkJudgeAttemptStatus.Running), cancellationToken)
                               .ConfigureAwait(false)

            // A comparison names TWO runs and its work item names only the canonical first, so the work-item guard
            // above sees a live comparison when this run is the A side and is blind to it when the run is the B side.
            // Asking the comparison rows themselves is the only guard that covers both.
            || await _dbContext.BenchmarkComparisons.AnyAsync(entity => (entity.RunAId == runId || entity.RunBId == runId)
                                                                        && (entity.Status == BenchmarkJudgeAttemptStatus.Queued
                                                                            || entity.Status == BenchmarkJudgeAttemptStatus.Running), cancellationToken)
                               .ConfigureAwait(false))
        {
            throw new BenchmarkConflictException("ActiveRun");
        }

        // Foreign keys are not enforced on this database, so the order below IS the referential integrity: the run
        // stops pointing at its attempt, then comparisons, work items, judge and fidelity attempts, then the run
        // itself. Anything left out of that list does not error — it simply outlives its run for good.
        var projectId = run.ProjectId;
        run.CurrentJudgeAttemptId = null;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await DeleteComparisonsOfAsync(runId, projectId, cancellationToken).ConfigureAwait(false);
        await _dbContext.BenchmarkWorkItems.Where(entity => entity.RunId == runId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await _dbContext.BenchmarkJudgeAttempts.Where(entity => entity.RunId == runId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await _dbContext.BenchmarkFidelityAttempts.Where(entity => entity.RunId == runId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        // The deletes intentionally bypass the tracker: this scope may have materialized the required work/run
        // relationship earlier, and mixing ExecuteDelete for the child with tracked Remove for the parent makes EF
        // interpret the already-deleted child as a severed required association.
        _dbContext.ChangeTracker.Clear();
        _ = await _dbContext.BenchmarkRuns.Where(entity => entity.Id == runId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);

        // The cohort was defined by runs that no longer exist. Leaving the reference key behind would silently keep
        // the next run's judging out of the ranking for a runtime nothing is measured against any more.
        if (!await _dbContext.BenchmarkRuns.AnyAsync(entity => entity.ProjectId == projectId, cancellationToken).ConfigureAwait(false))
        {
            await ResetCurrentCohortAsync(projectId, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Removes every comparison the run took part in — as the A side or the B side — together with the work items
    ///     that carry them, then bumps each affected revision's <c>ComparisonSetVersion</c> and retires the project's
    ///     active fits.
    ///     <para>
    ///         Deleting only by <see cref="BenchmarkWorkItem.RunId" /> stranded half of them: a comparison's work item
    ///         names the canonical FIRST run, so deleting the B side left comparison rows pointing at a run that no
    ///         longer exists and a published fit ranking it. The version bump is what makes the surviving fit read
    ///         stale; deactivating it is what makes the next planner pass re-fit the cohort that is actually left,
    ///         because a fit whose fitted set names a deleted run is not a ranking of anything.
    ///     </para>
    /// </summary>
    private async Task DeleteComparisonsOfAsync(Guid runId, Guid projectId, CancellationToken cancellationToken)
    {
        var affected = await _dbContext.BenchmarkComparisons.AsNoTracking()
                                       .Where(entity => entity.RunAId == runId || entity.RunBId == runId)
                                       .Select(entity => new
                                       {
                                           entity.Id,
                                           entity.PolicyRevisionId
                                       })
                                       .ToArrayAsync(cancellationToken)
                                       .ConfigureAwait(false);
        if (affected.Length == 0)
        {
            return;
        }

        var comparisonIds = affected.Select(static entry => entry.Id).ToArray();
        var revisionIds = affected.Select(static entry => entry.PolicyRevisionId).Distinct().ToArray();
        _ = await _dbContext.BenchmarkWorkItems
                            .Where(entity => entity.ComparisonId != null && comparisonIds.Contains(entity.ComparisonId.Value))
                            .ExecuteDeleteAsync(cancellationToken)
                            .ConfigureAwait(false);
        _ = await _dbContext.BenchmarkComparisons.Where(entity => comparisonIds.Contains(entity.Id))
                            .ExecuteDeleteAsync(cancellationToken)
                            .ConfigureAwait(false);
        _ = await _dbContext.BenchmarkJudgePolicyRevisions.Where(entity => revisionIds.Contains(entity.Id))
                            .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.ComparisonSetVersion, entity => entity.ComparisonSetVersion + 1),
                                cancellationToken)
                            .ConfigureAwait(false);
        _ = await _dbContext.BenchmarkPairwiseFits.Where(entity => entity.ProjectId == projectId && entity.IsActive)
                            .ExecuteUpdateAsync(setters => setters
                                                           .SetProperty(entity => entity.IsActive, false)
                                                           .SetProperty(entity => entity.Version, entity => entity.Version + 1), cancellationToken)
                            .ConfigureAwait(false);
    }
}
