namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class BenchmarkStore
{
    private async Task<(BenchmarkJudgePolicyRevision Revision, bool WasCreated)?> ApplyJudgePolicyChangeAsync(BenchmarkProject project,
        BenchmarkJudgePolicyChangeInput change,
        long now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.PolicyJson is not { } policyJson)
        {
            project.CurrentJudgePolicyRevisionId = null;
            return null;
        }

        if (policyJson.IsEmpty)
        {
            throw new BenchmarkValidationException("A judge policy cannot be empty.");
        }

        EnsurePolicyHash(change.PolicyHash);
        var current = project.CurrentJudgePolicyRevisionId is { } currentRevisionId
            ? await RequireJudgePolicyRevisionAsync(currentRevisionId, cancellationToken).ConfigureAwait(false)
            : null;

        // Same rule as an explicit activation: re-pointing at the policy the project already holds must not reset the
        // cohort, or saving an unrelated field edit would drop every ranked run out of the ranking.
        if (current is not null && string.Equals(current.PolicyHash, change.PolicyHash, StringComparison.Ordinal))
        {
            return (current, false);
        }

        return await RepointJudgePolicyAsync(project, policyJson, change.PolicyHash, now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Get-or-creates the revision for this policy, starts it on a fresh cohort, and points the project at it. The
    ///     caller owns the transaction and the project version bump.
    /// </summary>
    private async Task<(BenchmarkJudgePolicyRevision Revision, bool WasCreated)> RepointJudgePolicyAsync(BenchmarkProject project,
        ReadOnlyMemory<byte> policyJson,
        string policyHash,
        long now,
        CancellationToken cancellationToken)
    {
        var (revision, wasCreated) = await GetOrCreateJudgePolicyRevisionAsync(project.Id, policyJson, policyHash, now, cancellationToken).ConfigureAwait(false);
        if (!wasCreated)
        {
            // A revision the project has held before starts a fresh cohort; a brand new one is already at generation 1.
            revision.ReferenceExecutionKey = null;
            revision.CohortGeneration = checked(revision.CohortGeneration + 1);
        }

        project.CurrentJudgePolicyRevisionId = revision.Id;
        return (revision, wasCreated);
    }

    /// <summary>
    ///     The project's eligible runs and — with a seed — one fresh Queued attempt each, inserted inside the caller's
    ///     transaction. Enqueuing here rather than in a follow-up loop is what makes a cohort reset all-or-nothing: a
    ///     reset that committed with only some attempts enqueued would rank a cohort against runs never re-judged.
    ///     No already-applied guard: the caller has just reset the cohort, and every eligible run belongs to it.
    ///     <para>
    ///         A seed that clears <see cref="BenchmarkJudgeAttemptSeed.SeedPointwiseAttempts" /> reports the same
    ///         eligible set and inserts nothing: a pairwise cohort is judged by comparisons, and a pointwise attempt
    ///         queued beside them is a judging the mode never asked for.
    ///     </para>
    /// </summary>
    private async Task<IReadOnlyList<Guid>> EnqueueCohortAttemptsAsync(Guid projectId,
        BenchmarkJudgePolicyRevision revision,
        BenchmarkJudgeAttemptSeed? seed,
        long now,
        CancellationToken cancellationToken)
    {
        if (seed is null or { SeedPointwiseAttempts: false })
        {
            return await SucceededRunIdsAsync(projectId, cancellationToken).ConfigureAwait(false);
        }

        // Tracked, because each run takes a new attempt pointer and a version bump. The caller has already refused
        // the call if any attempt of the project is active, so no eligible run can be mid-judging here.
        var runs = await _dbContext.BenchmarkRuns
                                   .Where(entity => entity.ProjectId == projectId
                                                    && entity.PrimaryStatus == BenchmarkPrimaryStatus.Succeeded
                                                    && entity.OutputPartsJson != null)
                                   .OrderBy(entity => entity.CreatedAtUtc)
                                   .ThenBy(entity => entity.Id)
                                   .ToListAsync(cancellationToken)
                                   .ConfigureAwait(false);
        foreach (var run in runs)
        {
            _ = await InsertJudgeAttemptAsync(run, revision, seed.RuntimeJson, seed.RuntimeUnresolvedReason, seed.LaunchIntent, now, cancellationToken)
                .ConfigureAwait(false);
            run.Version++;
            run.UpdatedAtUtc = now;
        }

        return runs.Select(run => run.Id).ToArray();
    }

    /// <summary>The project's succeeded runs with stored output — exactly the set a re-judge must cover.</summary>
    private async Task<IReadOnlyList<Guid>> SucceededRunIdsAsync(Guid projectId, CancellationToken cancellationToken) =>
        await _dbContext.BenchmarkRuns.AsNoTracking()
                        .Where(entity => entity.ProjectId == projectId
                                         && entity.PrimaryStatus == BenchmarkPrimaryStatus.Succeeded
                                         && entity.OutputPartsJson != null)
                        .OrderBy(entity => entity.CreatedAtUtc)
                        .ThenBy(entity => entity.Id)
                        .Select(entity => entity.Id)
                        .ToArrayAsync(cancellationToken)
                        .ConfigureAwait(false);

    private async Task ResetCurrentCohortAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var project = await _dbContext.BenchmarkProjects.SingleOrDefaultAsync(entity => entity.Id == projectId, cancellationToken).ConfigureAwait(false);
        if (project?.CurrentJudgePolicyRevisionId is not { } revisionId)
        {
            return;
        }

        var revision = await _dbContext.BenchmarkJudgePolicyRevisions.SingleOrDefaultAsync(entity => entity.Id == revisionId, cancellationToken).ConfigureAwait(false);
        if (revision is null)
        {
            return;
        }

        revision.ReferenceExecutionKey = null;
        revision.CohortGeneration = checked(revision.CohortGeneration + 1);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureNoActiveJudgeAttemptsAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var active = await (from attempt in _dbContext.BenchmarkJudgeAttempts.AsNoTracking()
            join run in _dbContext.BenchmarkRuns.AsNoTracking() on attempt.RunId equals run.Id
            where run.ProjectId == projectId
                  && (attempt.Status == BenchmarkJudgeAttemptStatus.Queued || attempt.Status == BenchmarkJudgeAttemptStatus.Running)
            select attempt.Id).AnyAsync(cancellationToken).ConfigureAwait(false);
        if (active)
        {
            throw new BenchmarkConflictException("JudgeAttemptsActive");
        }
    }

    private async Task<BenchmarkJudgePolicyRevision> RequireJudgePolicyRevisionAsync(Guid revisionId, CancellationToken cancellationToken) =>
        await _dbContext.BenchmarkJudgePolicyRevisions.SingleOrDefaultAsync(entity => entity.Id == revisionId, cancellationToken).ConfigureAwait(false)
        ?? throw new BenchmarkNotFoundException("Benchmark judge policy revision was not found.");

    private async Task<BenchmarkJudgeAttempt> RequireJudgeAttemptAsync(Guid attemptId, CancellationToken cancellationToken) =>
        await _dbContext.BenchmarkJudgeAttempts.SingleOrDefaultAsync(entity => entity.Id == attemptId, cancellationToken).ConfigureAwait(false)
        ?? throw new BenchmarkNotFoundException("Benchmark judge attempt was not found.");

    private async Task<BenchmarkFidelityAttempt> RequireFidelityAttemptAsync(Guid attemptId, CancellationToken cancellationToken) =>
        await _dbContext.BenchmarkFidelityAttempts.SingleOrDefaultAsync(entity => entity.Id == attemptId, cancellationToken).ConfigureAwait(false)
        ?? throw new BenchmarkNotFoundException("Benchmark fidelity attempt was not found.");

    private async Task<BenchmarkJudgeComparison> RequireComparisonAsync(Guid comparisonId, CancellationToken cancellationToken) =>
        await _dbContext.BenchmarkComparisons.SingleOrDefaultAsync(entity => entity.Id == comparisonId, cancellationToken).ConfigureAwait(false)
        ?? throw new BenchmarkNotFoundException("Benchmark comparison was not found.");
}
