namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class BenchmarkStore
{
    public Task MarkComparisonFailedAsync(long queueSequence, long expectedWorkVersion, string errorMessage, CancellationToken cancellationToken = default) =>
        TerminalizeComparisonWorkAsync(queueSequence, expectedWorkVersion, BenchmarkJudgeAttemptStatus.Failed, BenchmarkWorkStatus.Failed,
            Sanitize(errorMessage), success: null, cancellationToken);

    public Task MarkComparisonCancelledAsync(long queueSequence, long expectedWorkVersion, CancellationToken cancellationToken = default) =>
        TerminalizeComparisonWorkAsync(queueSequence, expectedWorkVersion, BenchmarkJudgeAttemptStatus.Cancelled, BenchmarkWorkStatus.Cancelled,
            errorMessage: null, success: null, cancellationToken);

    public Task MarkComparisonSucceededAsync(BenchmarkComparisonSuccessCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Verdict is not (VerdictA or VerdictB or VerdictTie))
        {
            throw new BenchmarkValidationException("A pairwise verdict must be 'a', 'b' or 'tie'.");
        }

        return TerminalizeComparisonWorkAsync(command.QueueSequence, command.ExpectedWorkVersion, BenchmarkJudgeAttemptStatus.Succeeded,
            BenchmarkWorkStatus.Succeeded, errorMessage: null, command, cancellationToken);
    }

    private async Task TerminalizeComparisonWorkAsync(long queueSequence,
        long expectedWorkVersion,
        BenchmarkJudgeAttemptStatus comparisonStatus,
        BenchmarkWorkStatus workStatus,
        string? errorMessage,
        BenchmarkComparisonSuccessCommand? success,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var work = await _dbContext.BenchmarkWorkItems.SingleOrDefaultAsync(entity => entity.QueueSequence == queueSequence, cancellationToken).ConfigureAwait(false)
                   ?? throw new BenchmarkNotFoundException("Benchmark work item was not found.");
        if (work.Kind != BenchmarkWorkKind.Comparison)
        {
            throw new BenchmarkConflictException("InvalidComparisonTransition");
        }

        EnsureVersion(work.Version, expectedWorkVersion);
        var now = Now();
        TerminalizeWork(work, workStatus, errorMessage, now);
        var comparison = await TerminalizeComparisonAsync(work.ComparisonId, comparisonStatus, errorMessage, now, cancellationToken).ConfigureAwait(false);
        if (comparison is not null && success is not null)
        {
            comparison.Verdict = success.Verdict;
            comparison.ResultJson = success.ResultJson is { IsEmpty: false } result ? result.ToArray() : null;
            comparison.AnswerATruncated = success.AnswerATruncated;
            comparison.AnswerBTruncated = success.AnswerBTruncated;

            // The cohort is claimed by the first SUCCESS of the live generation, exactly as a pointwise attempt claims
            // it — and it MUST be claimed here, because a pairwise cohort has no judge attempts to claim it instead:
            // an unclaimed reference key refuses every fit over the cohort as execution-identity-incomplete.
            if (comparison.JudgeExecutionKey is { Length: > 0 } executionKey)
            {
                _ = await TryPromoteReferenceExecutionKeyAsync(comparison.PolicyRevisionId, comparison.CohortGeneration, executionKey, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<BenchmarkPairwiseCohortState> GetPairwiseCohortAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var project = await _dbContext.BenchmarkProjects.AsNoTracking()
                                      .Select(entity => new
                                      {
                                          entity.Id,
                                          entity.Version,
                                          entity.CurrentJudgePolicyRevisionId
                                      })
                                      .SingleOrDefaultAsync(entity => entity.Id == projectId, cancellationToken)
                                      .ConfigureAwait(false)
                      ?? throw new BenchmarkNotFoundException("Benchmark project was not found.");
        if (project.CurrentJudgePolicyRevisionId is not { } revisionId)
        {
            return new BenchmarkPairwiseCohortState(null, 0, 0, null, project.Version, [], []);
        }

        var revision = await _dbContext.BenchmarkJudgePolicyRevisions.AsNoTracking()
                                       .Select(entity => new
                                       {
                                           entity.Id,
                                           entity.CohortGeneration,
                                           entity.ComparisonSetVersion,
                                           entity.ReferenceExecutionKey
                                       })
                                       .SingleAsync(entity => entity.Id == revisionId, cancellationToken)
                                       .ConfigureAwait(false);

        // Flat columns only: eligibility is decided from the stop reason through the SHARED predicates, which are C#
        // and are therefore applied after the read rather than translated into a second, drifting copy of the rule.
        var runs = await _dbContext.BenchmarkRuns.AsNoTracking()
                                   .Where(entity => entity.ProjectId == projectId
                                                    && entity.PrimaryStatus == BenchmarkPrimaryStatus.Succeeded
                                                    && !entity.IsWarmup
                                                    && entity.OutputPartsJson != null)
                                   .OrderBy(entity => entity.CreatedAtUtc)
                                   .ThenBy(entity => entity.Id)
                                   .Select(entity => new
                                   {
                                       entity.Id,
                                       entity.PrimaryStopReason
                                   })
                                   .ToArrayAsync(cancellationToken)
                                   .ConfigureAwait(false);
        var candidates = runs.Where(static run => !BenchmarkPrimaryStopReasons.IsTruncated(run.PrimaryStopReason)
                                                  && !BenchmarkPrimaryStopReasons.IsIncomplete(run.PrimaryStopReason))
                             .Select(static run => new BenchmarkPairwiseCandidate(run.Id, TaskCaseId: null, TaskInputHash: string.Empty))
                             .ToArray();
        var comparisons = await ProjectComparisons(_dbContext.BenchmarkComparisons.AsNoTracking()
                                                             .Where(entity => entity.PolicyRevisionId == revisionId
                                                                              && entity.CohortGeneration == revision.CohortGeneration)
                                                             .OrderBy(entity => entity.Sequence))
                                .ToArrayAsync(cancellationToken)
                                .ConfigureAwait(false);
        return new BenchmarkPairwiseCohortState(revision.Id,
            revision.CohortGeneration,
            revision.ComparisonSetVersion,
            revision.ReferenceExecutionKey,
            project.Version,
            candidates,
            comparisons);
    }

    public async Task<int> EnsureComparisonsAsync(Guid projectId,
        IReadOnlyList<BenchmarkPairwiseSlot> slots,
        ReadOnlyMemory<byte>? judgeRuntimeJson,
        BenchmarkRunLaunchIntent? launchIntent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (slots.Count == 0)
        {
            return 0;
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var project = await RequireProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (project.CurrentJudgePolicyRevisionId is not { } revisionId)
        {
            return 0;
        }

        var revision = await RequireJudgePolicyRevisionAsync(revisionId, cancellationToken).ConfigureAwait(false);
        var existing = await _dbContext.BenchmarkComparisons.AsNoTracking()
                                       .Where(entity => entity.PolicyRevisionId == revisionId && entity.CohortGeneration == revision.CohortGeneration)
                                       .Select(entity => new
                                       {
                                           entity.RunAId,
                                           entity.RunBId,
                                           entity.Order,
                                           entity.Status,
                                           entity.Sequence,
                                           entity.AttemptSequence
                                       })
                                       .ToArrayAsync(cancellationToken)
                                       .ConfigureAwait(false);

        // A slot is taken while it holds a live-or-succeeded comparison. A terminal FAILED one leaves it free, which
        // is the whole reason the live-slot uniqueness index is filtered on status: a cancelled comparison must be
        // re-enqueueable at the next attempt sequence, or its cohort never completes and never publishes a score.
        var taken = existing.Where(static entry => entry.Status is BenchmarkJudgeAttemptStatus.Queued
                                or BenchmarkJudgeAttemptStatus.Running
                                or BenchmarkJudgeAttemptStatus.Succeeded)
                            .Select(static entry => (entry.RunAId, entry.RunBId, entry.Order))
                            .ToHashSet();
        var sequence = existing.Length == 0 ? 0 : existing.Max(static entry => entry.Sequence);
        var now = Now();
        var created = 0;
        foreach (var slot in slots)
        {
            foreach (var order in ComparisonOrders)
            {
                if (taken.Contains((slot.RunAId, slot.RunBId, order)))
                {
                    continue;
                }

                var attemptSequence = existing.Where(entry => entry.RunAId == slot.RunAId && entry.RunBId == slot.RunBId && entry.Order == order)
                                              .Select(static entry => entry.AttemptSequence)
                                              .DefaultIfEmpty(0)
                                              .Max()
                                      + 1;
                var comparison = new BenchmarkJudgeComparison
                {
                    Id = Guid.NewGuid(),
                    ProjectId = projectId,
                    PolicyRevisionId = revisionId,
                    CohortGeneration = revision.CohortGeneration,
                    TaskCaseId = slot.TaskCaseId,
                    TaskInputHash = slot.TaskInputHash,
                    RunAId = slot.RunAId,
                    RunBId = slot.RunBId,
                    Order = order,
                    AttemptSequence = attemptSequence,
                    Sequence = ++sequence,
                    JudgeRuntimeJson = judgeRuntimeJson?.ToArray(),
                    Status = BenchmarkJudgeAttemptStatus.Queued,
                    Variant = launchIntent?.Variant,
                    KvCacheType = launchIntent?.KvCacheType,
                    KvCacheTypeSource = launchIntent?.KvCacheTypeSource,
                    KvAutoReason = launchIntent?.KvAutoReason,
                    FlashAttentionMode = launchIntent?.FlashAttentionMode,
                    IntendedLaunchIdentity = launchIntent?.IntendedLaunchIdentity,
                    IntendedExecutableSha256 = launchIntent?.IntendedExecutableSha256,
                    LaunchIdentityScheme = launchIntent?.LaunchIdentityScheme,
                    EnqueuedAtUtc = now,
                    Version = 1
                };
                _dbContext.BenchmarkComparisons.Add(comparison);

                // A comparison names two runs, so its work item names the canonical first one and every comparison
                // lifecycle call is keyed by queue sequence instead — "the run's comparison item" is not well formed.
                _dbContext.BenchmarkWorkItems.Add(new BenchmarkWorkItem
                {
                    RunId = slot.RunAId,
                    Kind = BenchmarkWorkKind.Comparison,
                    ComparisonId = comparison.Id,
                    Status = BenchmarkWorkStatus.Queued,
                    Attempt = 1,
                    Version = 1,
                    EnqueuedAtUtc = now
                });
                created++;
            }
        }

        if (created == 0)
        {
            return 0;
        }

        revision.ComparisonSetVersion = checked(revision.ComparisonSetVersion + 1);
        try
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (BenchmarkConflictException conflict) when (string.Equals(conflict.Code, "DuplicateWork", StringComparison.Ordinal))
        {
            // A concurrent caller created the same slots. The transaction is abandoned and the cohort is whatever that
            // caller committed — re-reading is the caller's next step, and the whole pass is idempotent by design.
            _dbContext.ChangeTracker.Clear();
            return 0;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return created;
    }

    public async Task<BenchmarkComparisonRecord?> GetComparisonAsync(Guid comparisonId, CancellationToken cancellationToken = default)
    {
        var comparison = await _dbContext.BenchmarkComparisons.AsNoTracking()
                                         .SingleOrDefaultAsync(entity => entity.Id == comparisonId, cancellationToken)
                                         .ConfigureAwait(false);
        return comparison is null ? null : ToRecord(comparison, CopyOptional(comparison.JudgeRuntimeJson));
    }

    public async Task<bool> MarkComparisonLaunchReadyAsync(Guid comparisonId,
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
        if (work is null
            || work.Kind != BenchmarkWorkKind.Comparison
            || work.ComparisonId != comparisonId
            || !((work.Status == BenchmarkWorkStatus.Running && work.Version == claimedWorkVersion)
                 || (work.Status == BenchmarkWorkStatus.Cancelled && work.Version == claimedWorkVersion + 1)))
        {
            return false;
        }

        var comparison = await RequireComparisonAsync(comparisonId, cancellationToken).ConfigureAwait(false);
        if (comparison.LaunchReceiptJson is not null || comparison.EnvironmentFactsJson is not null)
        {
            return false;
        }

        comparison.LaunchReceiptJson = command.ReceiptJson is null ? null : Encoding.UTF8.GetBytes(command.ReceiptJson);
        comparison.EnvironmentFactsJson = Encoding.UTF8.GetBytes(command.EnvironmentFactsJson);
        comparison.ReceiptHash = command.ReceiptHash;
        comparison.EnvironmentFactsHash = command.EnvironmentFactsHash;
        comparison.EffectiveLaunchIdentity = command.EffectiveLaunchIdentity;
        comparison.EffectiveBackend = command.EffectiveBackend;
        comparison.PlacementOffloaded = command.PlacementOffloaded;
        comparison.PlacementTotal = command.PlacementTotal;
        comparison.LaunchExecutableSha256 = command.ExecutableSha256;
        comparison.LaunchHasAuxAssets = command.HasAuxAssets;
        comparison.LaunchKvCacheTypeSource = command.KvCacheTypeSource;

        // Same posture as the judge attempt: NULL stays NULL. An execution this node cannot fully describe never
        // joins a cohort, and a fit over one mismatched comparison is refused whole rather than quietly trimmed.
        comparison.JudgeExecutionKey = judgeExecutionKey;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> PublishPairwiseFitAsync(BenchmarkPairwiseFitCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var scope = await _dbContext.BenchmarkPairwiseFits
                                    .Where(entity => entity.PolicyRevisionId == command.PolicyRevisionId
                                                     && entity.CohortGeneration == command.CohortGeneration
                                                     && entity.IsActive)
                                    .ToListAsync(cancellationToken)
                                    .ConfigureAwait(false);
        foreach (var previous in scope.Where(entity => entity.TaskCaseId == command.TaskCaseId))
        {
            previous.IsActive = false;
            previous.Version++;
        }

        _dbContext.BenchmarkPairwiseFits.Add(new BenchmarkPairwiseFit
        {
            Id = Guid.NewGuid(),
            ProjectId = command.ProjectId,
            PolicyRevisionId = command.PolicyRevisionId,
            CohortGeneration = command.CohortGeneration,
            TaskCaseId = command.TaskCaseId,
            FitKey = command.FitKey,
            JudgeExecutionKey = command.JudgeExecutionKey,
            ComparisonSetVersion = command.ComparisonSetVersion,
            FittedSetJson = command.FittedSetJson,
            ScoresJson = command.ScoresJson,
            Iterations = command.Iterations,
            BootstrapReplicates = command.BootstrapReplicates,
            IsActive = true,
            CreatedAtUtc = Now(),
            Version = 1
        });
        try
        {
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (BenchmarkConflictException conflict) when (string.Equals(conflict.Code, "DuplicateWork", StringComparison.Ordinal))
        {
            // Another terminalization computed the same fit key from the same inputs and published first: same set,
            // same numbers. The whole transaction is abandoned and the standing row is left exactly as it is.
            _dbContext.ChangeTracker.Clear();
            return false;
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<BenchmarkPairwiseFitRecord?> GetActivePairwiseFitAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var revisionId = await _dbContext.BenchmarkProjects.AsNoTracking()
                                         .Where(entity => entity.Id == projectId)
                                         .Select(entity => entity.CurrentJudgePolicyRevisionId)
                                         .SingleOrDefaultAsync(cancellationToken)
                                         .ConfigureAwait(false);
        if (revisionId is not { } currentRevisionId)
        {
            return null;
        }

        var generation = await _dbContext.BenchmarkJudgePolicyRevisions.AsNoTracking()
                                         .Where(entity => entity.Id == currentRevisionId)
                                         .Select(entity => entity.CohortGeneration)
                                         .SingleAsync(cancellationToken)
                                         .ConfigureAwait(false);
        return await ActiveFitAsync(currentRevisionId, generation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     The scope's active fit: one indexed read narrowed to the case in memory. At most one row per case survives
    ///     the filtered unique index, so this is a handful of rows and never a scan of the comparisons behind them.
    /// </summary>
    private async Task<BenchmarkPairwiseFitRecord?> ActiveFitAsync(Guid revisionId, int generation, CancellationToken cancellationToken)
    {
        var fits = await _dbContext.BenchmarkPairwiseFits.AsNoTracking()
                                   .Where(entity => entity.PolicyRevisionId == revisionId && entity.CohortGeneration == generation && entity.IsActive)
                                   .ToArrayAsync(cancellationToken)
                                   .ConfigureAwait(false);
        var fit = Array.Find(fits, static entity => entity.TaskCaseId is null);
        return fit is null
            ? null
            : new BenchmarkPairwiseFitRecord(fit.Id,
                fit.ProjectId,
                fit.PolicyRevisionId,
                fit.CohortGeneration,
                fit.TaskCaseId,
                fit.FitKey,
                fit.JudgeExecutionKey,
                fit.ComparisonSetVersion,
                fit.FittedSetJson,
                fit.ScoresJson,
                fit.Iterations,
                fit.BootstrapReplicates,
                fit.CreatedAtUtc);
    }

    public async Task<double?> GetMedianJudgeDurationSecondsAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var durations = await (from attempt in _dbContext.BenchmarkJudgeAttempts.AsNoTracking()
                join run in _dbContext.BenchmarkRuns.AsNoTracking() on attempt.RunId equals run.Id
                where run.ProjectId == projectId
                      && attempt.Status == BenchmarkJudgeAttemptStatus.Succeeded
                      && attempt.StartedAtUtc != null
                      && attempt.CompletedAtUtc != null
                select attempt.CompletedAtUtc!.Value - attempt.StartedAtUtc!.Value).ToArrayAsync(cancellationToken)
                                                                                   .ConfigureAwait(false);
        if (durations.Length == 0)
        {
            return null;
        }

        Array.Sort(durations);
        var middle = durations.Length / 2;
        var milliseconds = durations.Length % 2 == 1 ? durations[middle] : (durations[middle - 1] + durations[middle]) / 2.0;
        return milliseconds / 1000.0;
    }

    public async Task<IReadOnlyList<Guid>> ListJudgedProjectIdsAsync(CancellationToken cancellationToken = default) =>
        await _dbContext.BenchmarkProjects.AsNoTracking()
                        .Where(entity => entity.CurrentJudgePolicyRevisionId != null)
                        .Select(entity => entity.Id)
                        .ToArrayAsync(cancellationToken)
                        .ConfigureAwait(false);

    /// <summary>
    ///     Comparison rows WITHOUT the encrypted judge runtime: a verdict matrix must not decrypt one payload per row.
    ///     The single-comparison read adds it back for the executor.
    /// </summary>
    private static IQueryable<BenchmarkComparisonRecord> ProjectComparisons(IQueryable<BenchmarkJudgeComparison> query) =>
        query.Select(entity => new BenchmarkComparisonRecord(entity.Id,
            entity.ProjectId,
            entity.PolicyRevisionId,
            entity.CohortGeneration,
            entity.TaskCaseId,
            entity.TaskInputHash,
            entity.RunAId,
            entity.RunBId,
            entity.Order,
            entity.AttemptSequence,
            entity.Sequence,
            entity.Status,
            entity.Verdict,
            entity.AnswerATruncated,
            entity.AnswerBTruncated,
            entity.JudgeExecutionKey,
            entity.ErrorMessage,
            null,
            entity.EnqueuedAtUtc,
            entity.StartedAtUtc,
            entity.CompletedAtUtc,
            entity.Version));

    private static BenchmarkComparisonRecord ToRecord(BenchmarkJudgeComparison entity, ReadOnlyMemory<byte>? judgeRuntimeJson) =>
        new(entity.Id,
            entity.ProjectId,
            entity.PolicyRevisionId,
            entity.CohortGeneration,
            entity.TaskCaseId,
            entity.TaskInputHash,
            entity.RunAId,
            entity.RunBId,
            entity.Order,
            entity.AttemptSequence,
            entity.Sequence,
            entity.Status,
            entity.Verdict,
            entity.AnswerATruncated,
            entity.AnswerBTruncated,
            entity.JudgeExecutionKey,
            entity.ErrorMessage,
            judgeRuntimeJson,
            entity.EnqueuedAtUtc,
            entity.StartedAtUtc,
            entity.CompletedAtUtc,
            entity.Version,
            // Only the single-comparison read carries the intent: it is what the executor needs, and the verdict
            // matrix must not pay for a value no row in it reads.
            ToIntent(entity.Variant, entity.KvCacheType, entity.KvCacheTypeSource, entity.KvAutoReason,
                entity.FlashAttentionMode, entity.IntendedLaunchIdentity, entity.IntendedExecutableSha256,
                entity.LaunchIdentityScheme));
}
