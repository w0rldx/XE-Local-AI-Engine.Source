namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class BenchmarkStore
{
    public async Task<BenchmarkJudgePolicyActivation> ActivateJudgePolicyAsync(Guid projectId,
        long expectedProjectVersion,
        ReadOnlyMemory<byte> policyJson,
        string policyHash,
        BenchmarkJudgeAttemptSeed? cohortAttemptSeed = null,
        CancellationToken cancellationToken = default)
    {
        if (policyJson.IsEmpty)
        {
            throw new BenchmarkValidationException("A judge policy cannot be empty.");
        }

        EnsurePolicyHash(policyHash);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var project = await RequireProjectAsync(projectId, cancellationToken);
        EnsureVersion(project.Version, expectedProjectVersion);
        await EnsureNoActiveJudgeAttemptsAsync(projectId, cancellationToken);

        var current = project.CurrentJudgePolicyRevisionId is { } currentRevisionId
            ? await RequireJudgePolicyRevisionAsync(currentRevisionId, cancellationToken)
            : null;

        // Re-activating the policy the project already judges under changes nothing — in particular it must not reset
        // the cohort, or a no-op save would drop every ranked run out of the ranking.
        if (current is not null && string.Equals(current.PolicyHash, policyHash, StringComparison.Ordinal))
        {
            return new BenchmarkJudgePolicyActivation
            {
                Revision = ToRecord(current, includePayload: true),
                WasCreated = false,
                SucceededRunIds = []
            };
        }

        var now = Now();
        var (revision, wasCreated) = await RepointJudgePolicyAsync(project, policyJson, policyHash, now, cancellationToken);
        project.Version++;
        project.UpdatedAtUtc = now;
        var runIds = await EnqueueCohortAttemptsAsync(projectId, revision, cohortAttemptSeed, now, cancellationToken);
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BenchmarkJudgePolicyActivation
        {
            Revision = ToRecord(revision, includePayload: true),
            WasCreated = wasCreated,
            SucceededRunIds = runIds
        };
    }

    public async Task DisableJudgePolicyAsync(Guid projectId, long expectedProjectVersion, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var project = await RequireProjectAsync(projectId, cancellationToken);
        EnsureVersion(project.Version, expectedProjectVersion);
        await EnsureNoActiveJudgeAttemptsAsync(projectId, cancellationToken);
        project.CurrentJudgePolicyRevisionId = null;
        project.Version++;
        project.UpdatedAtUtc = Now();
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<BenchmarkProjectFidelityChange> UpdateProjectFidelityAsync(Guid projectId,
        long expectedProjectVersion,
        BenchmarkProjectFidelityInput input,
        bool measureExisting = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var project = await RequireProjectAsync(projectId, cancellationToken);
        EnsureVersion(project.Version, expectedProjectVersion);

        // No freeze check, on purpose. See IBenchmarkStore: a frozen project refuses edits to what its runs were measured AGAINST, while these settings decide what
        // gets measured next, and every stored number keeps the comparability digest it was measured under, so a change here makes old figures stale, not wrong.
        var now = Now();
        project.FidelityEnabled = input.FidelityEnabled;
        project.FidelityKldEnabled = input.FidelityKldEnabled;
        project.FidelityChunks = input.FidelityChunks;
        project.FidelityKldBaseModelName = input.FidelityKldBaseModelName;
        project.FidelityKldBaseFingerprint = input.FidelityKldBaseFingerprint;
        project.Version++;
        project.UpdatedAtUtc = now;

        var frozen = await _dbContext.BenchmarkRuns.AnyAsync(entity => entity.ProjectId == projectId, cancellationToken);
        var enqueued = measureExisting && input.FidelityEnabled
            ? await EnqueueMissingFidelityAsync(project, now, cancellationToken)
            : [];
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BenchmarkProjectFidelityChange
        {
            Project = ToRecord(project, frozen),
            EnqueuedRunIds = enqueued
        };
    }

    /// <summary>
    ///     Queues one fidelity measurement per succeeded cell that has none.
    /// </summary>
    /// <remarks>
    ///     The eligibility rule is freeze's own — non-warm-up, first of its repeat group — because a cell measured here
    ///     and a cell measured at freeze must mean the same thing. Runs that already have an attempt are skipped rather
    ///     than re-measured: a re-measure is the per-run route's job and costs GPU the operator did not ask for here.
    /// </remarks>
    private async Task<IReadOnlyList<Guid>> EnqueueMissingFidelityAsync(BenchmarkProject project, long now, CancellationToken cancellationToken)
    {
        var kind = project.FidelityKldEnabled ? FidelityKindKld : FidelityKindPerplexity;
        var candidates = await _dbContext.BenchmarkRuns
                                         .Where(entity => entity.ProjectId == project.Id
                                                          && entity.PrimaryStatus == BenchmarkPrimaryStatus.Succeeded
                                                          && !entity.IsWarmup
                                                          && (entity.RepeatIndex == null || entity.RepeatIndex == 1)
                                                          && !_dbContext.BenchmarkRuns.Any(other => other.ProjectId == entity.ProjectId
                                                                                                    && other.CellKey == entity.CellKey
                                                                                                    && other.TaskItemIndex < entity.TaskItemIndex)
                                                          && !_dbContext.BenchmarkFidelityAttempts.Any(attempt => attempt.RunId == entity.Id))
                                         .OrderBy(entity => entity.CreatedAtUtc)
                                         .ToListAsync(cancellationToken);
        foreach (var run in candidates)
        {
            // AppendFidelityWorkAsync already sets the projection to 'queued' and clears the error, which is exactly what an enqueued measurement is. Resetting it
            // to null here would undo that in the same transaction and leave the run reading as "fidelity was never asked for" while its item sat in the queue.
            _ = await AppendFidelityWorkAsync(run, kind, now, cancellationToken);
            run.Version++;
            run.UpdatedAtUtc = now;
        }

        return [.. candidates.Select(static run => run.Id)];
    }

    public async Task<BenchmarkJudgeAttemptRecord?> GetJudgeAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default) =>
        await _dbContext.BenchmarkJudgeAttempts.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == attemptId, cancellationToken) is { } attempt
            ? ToRecord(attempt)
            : null;

    public async Task<BenchmarkJudgePolicyRevisionRecord?> GetJudgePolicyRevisionAsync(Guid revisionId, CancellationToken cancellationToken = default) =>
        await _dbContext.BenchmarkJudgePolicyRevisions.AsNoTracking().SingleOrDefaultAsync(entity => entity.Id == revisionId, cancellationToken) is { } revision
            ? ToRecord(revision, includePayload: true)
            : null;

    public async Task<BenchmarkJudgePolicyRevisionRecord?> GetCurrentJudgePolicyRevisionAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        // Only the pointer column is read: resolving the current revision must not decrypt the project's core task.
        var revisionId = await _dbContext.BenchmarkProjects.AsNoTracking()
                                         .Where(entity => entity.Id == projectId)
                                         .Select(entity => entity.CurrentJudgePolicyRevisionId)
                                         .SingleOrDefaultAsync(cancellationToken);
        if (revisionId is null)
        {
            return null;
        }

        var revision = await _dbContext.BenchmarkJudgePolicyRevisions.AsNoTracking()
                                       .SingleOrDefaultAsync(entity => entity.Id == revisionId, cancellationToken);
        return revision is null ? null : ToRecord(revision, includePayload: true);
    }

    public async Task<IReadOnlyList<BenchmarkJudgePolicyRevisionRecord>> ListJudgePolicyRevisionsAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await _dbContext.BenchmarkJudgePolicyRevisions.AsNoTracking()
                        .Where(entity => entity.ProjectId == projectId)
                        .OrderBy(entity => entity.Revision)
                        // Column projection, not entity materialization: a history list must not decrypt one policy
                        // blob per row to render revision numbers and hashes.
                        .Select(entity => new BenchmarkJudgePolicyRevisionRecord
                        {
                            Id = entity.Id,
                            ProjectId = entity.ProjectId,
                            Revision = entity.Revision,
                            PolicyJson = null,
                            PolicyHash = entity.PolicyHash,
                            ReferenceExecutionKey = entity.ReferenceExecutionKey,
                            CohortGeneration = entity.CohortGeneration,
                            CreatedAtUtc = entity.CreatedAtUtc
                        })
                        .ToArrayAsync(cancellationToken);

    public async Task<BenchmarkJudgeAttemptRecord> EnqueueJudgeAttemptAsync(BenchmarkEnqueueJudgeAttemptCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var run = await RequireRunAsync(command.RunId, tracking: true, cancellationToken);
        EnsureVersion(run.Version, command.ExpectedRunVersion);
        if (run.PrimaryStatus != BenchmarkPrimaryStatus.Succeeded || run.OutputPartsJson is null)
        {
            throw new BenchmarkConflictException("PrimaryNotSucceeded");
        }

        var currentRevisionId = await _dbContext.BenchmarkProjects.AsNoTracking()
                                                .Where(entity => entity.Id == run.ProjectId)
                                                .Select(entity => entity.CurrentJudgePolicyRevisionId)
                                                .SingleOrDefaultAsync(cancellationToken);
        if (currentRevisionId != command.PolicyRevisionId)
        {
            throw new BenchmarkJudgePolicyChangedException("The requested judge policy revision is not the project's current one.");
        }

        var revision = await RequireJudgePolicyRevisionAsync(command.PolicyRevisionId, cancellationToken);
        var currentAttempt = run.CurrentJudgeAttemptId is { } currentAttemptId
            ? await RequireJudgeAttemptAsync(currentAttemptId, cancellationToken)
            : null;
        if (currentAttempt?.Status is BenchmarkJudgeAttemptStatus.Queued or BenchmarkJudgeAttemptStatus.Running)
        {
            throw new BenchmarkConflictException("JudgeAttemptActive");
        }

        // Already applied means: judged under this exact revision, in the live cohort generation, with the execution
        // the cohort is keyed on. Anything else — a stale generation, a different runtime — earns a fresh attempt.
        if (!command.Force
            && currentAttempt is { Status: BenchmarkJudgeAttemptStatus.Succeeded }
            && currentAttempt.PolicyRevisionId == revision.Id
            && currentAttempt.CohortGeneration == revision.CohortGeneration
            && currentAttempt.JudgeExecutionKey is not null
            && string.Equals(currentAttempt.JudgeExecutionKey, revision.ReferenceExecutionKey, StringComparison.Ordinal))
        {
            throw new BenchmarkConflictException("JudgePolicyAlreadyApplied");
        }

        var now = Now();
        var attempt = await InsertJudgeAttemptAsync(run,
            revision,
            command.RuntimeJson,
            command.RuntimeUnresolvedReason,
            command.LaunchIntent,
            now,
            cancellationToken);
        run.Version++;
        run.UpdatedAtUtc = now;
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ToRecord(attempt);
    }

    public async Task<BenchmarkJudgePolicyActivation> BeginProjectRejudgeAsync(Guid projectId,
        long expectedProjectVersion,
        BenchmarkJudgeAttemptSeed? cohortAttemptSeed = null,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var project = await RequireProjectAsync(projectId, cancellationToken);
        EnsureVersion(project.Version, expectedProjectVersion);
        await EnsureNoActiveJudgeAttemptsAsync(projectId, cancellationToken);
        if (project.CurrentJudgePolicyRevisionId is not { } revisionId)
        {
            throw new BenchmarkConflictException("JudgeDisabled");
        }

        // The seed's runtime was resolved for one revision. If the project has moved on since, rolling back is the
        // only honest answer: the caller re-resolves and retries rather than judging a cohort under a stale runtime.
        if (cohortAttemptSeed?.ExpectedJudgePolicyRevisionId is { } expectedRevisionId && expectedRevisionId != revisionId)
        {
            throw new BenchmarkJudgePolicyChangedException("The project's judge policy changed. Refresh and retry.");
        }

        // The reset and the attempts are committed together, so "move the cohort to the current runtime" can never
        // leave a partial set that would then rank against a key half of it never ran under.
        var now = Now();
        var revision = await RequireJudgePolicyRevisionAsync(revisionId, cancellationToken);
        revision.ReferenceExecutionKey = null;
        revision.CohortGeneration = checked(revision.CohortGeneration + 1);
        project.Version++;
        project.UpdatedAtUtc = now;
        var runIds = await EnqueueCohortAttemptsAsync(projectId, revision, cohortAttemptSeed, now, cancellationToken);
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new BenchmarkJudgePolicyActivation
        {
            Revision = ToRecord(revision, includePayload: true),
            WasCreated = false,
            SucceededRunIds = runIds
        };
    }

    public async Task<bool> TryPromoteReferenceExecutionKeyAsync(Guid revisionId,
        int cohortGeneration,
        string executionKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executionKey))
        {
            throw new BenchmarkValidationException("A judge execution key cannot be empty.");
        }

        // Insert-if-null compare-and-swap in one statement: whichever same-generation success gets there first defines
        // the cohort, and a reset (generation bump) makes every in-flight promotion attempt miss.
        var promoted = await _dbContext.BenchmarkJudgePolicyRevisions
                                       .Where(entity => entity.Id == revisionId
                                                        && entity.CohortGeneration == cohortGeneration
                                                        && entity.ReferenceExecutionKey == null)
                                       .ExecuteUpdateAsync(setters => setters.SetProperty(entity => entity.ReferenceExecutionKey, executionKey), cancellationToken);
        return promoted == 1;
    }
}
