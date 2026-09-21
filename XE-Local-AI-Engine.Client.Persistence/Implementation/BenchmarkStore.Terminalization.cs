namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class BenchmarkStore
{
    private async Task<BenchmarkRunRecord> TerminalizePrimaryNonSuccessAsync(Guid runId,
        long expectedRunVersion,
        BenchmarkPrimaryStatus status,
        BenchmarkWorkStatus workStatus,
        string errorMessage,
        long lastStreamSequence,
        string? primaryStopReason,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        await AcquireWorkCompletionAsync(runId, BenchmarkWorkKind.Primary, expectedRunVersion, cancellationToken);
        _dbContext.ChangeTracker.Clear();
        var run = await RequireRunAsync(runId, tracking: true, cancellationToken);
        if (run.PrimaryStatus == status)
        {
            return await ToRecordWithJudgeAsync(run, cancellationToken);
        }

        var work = await RequireWorkAsync(run.Id, BenchmarkWorkKind.Primary, cancellationToken);
        EnsureVersion(work.Version, expectedRunVersion);
        if (run.PrimaryStatus is not (BenchmarkPrimaryStatus.Running or BenchmarkPrimaryStatus.CancelRequested))
        {
            throw new BenchmarkConflictException("InvalidPrimaryTransition");
        }

        var now = Now();
        var reconciledStatus = run.PrimaryStatus == BenchmarkPrimaryStatus.CancelRequested
            ? BenchmarkPrimaryStatus.Cancelled
            : status;
        var reconciledWorkStatus = reconciledStatus == BenchmarkPrimaryStatus.Cancelled
            ? BenchmarkWorkStatus.Cancelled
            : workStatus;
        run.PrimaryStatus = reconciledStatus;
        run.PrimaryErrorMessage = reconciledStatus == BenchmarkPrimaryStatus.Cancelled ? null : Sanitize(errorMessage);

        // Only ever written, never cleared: a caller that cannot explain the failure leaves whatever the run already
        // knew about how generation stopped.
        if (primaryStopReason is { Length: > 0 } && reconciledStatus != BenchmarkPrimaryStatus.Cancelled)
        {
            run.PrimaryStopReason = primaryStopReason;
        }

        UpdateLastStreamSequence(run, lastStreamSequence);
        run.PrimaryCompletedAtUtc = now;
        run.Version++;
        run.UpdatedAtUtc = now;
        TerminalizeWork(work, reconciledWorkStatus, run.PrimaryErrorMessage, now);
        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return await ToRecordWithJudgeAsync(run, cancellationToken);
    }

    /// <summary>
    ///     Inserts one judging of <paramref name="run" /> plus its work item, and repoints the run at it.
    /// </summary>
    /// <remarks>
    ///     A missing <paramref name="runtimeJson" /> means the judge runtime could not be resolved: the attempt goes in
    ///     already Failed together with a terminal work item, so "attempt implies work item" holds without ever
    ///     queueing work that no claimant could execute.
    /// </remarks>
    private async Task<BenchmarkJudgeAttempt> InsertJudgeAttemptAsync(BenchmarkRun run,
        BenchmarkJudgePolicyRevision revision,
        ReadOnlyMemory<byte>? runtimeJson,
        string? runtimeUnresolvedReason,
        BenchmarkRunLaunchIntent? launchIntent,
        long now,
        CancellationToken cancellationToken)
    {
        var lastSequence = await _dbContext.BenchmarkJudgeAttempts
                                           .Where(entity => entity.RunId == run.Id)
                                           .Select(entity => (int?)entity.Sequence)
                                           .MaxAsync(cancellationToken)
                           ?? 0;
        var unresolved = runtimeJson is null;
        var failure = unresolved ? Sanitize(runtimeUnresolvedReason ?? UnresolvedJudgeRuntimeMessage) : null;
        var attempt = new BenchmarkJudgeAttempt
        {
            Id = Guid.NewGuid(),
            RunId = run.Id,
            Sequence = lastSequence + 1,
            PolicyRevisionId = revision.Id,
            CohortGeneration = revision.CohortGeneration,
            JudgeRuntimeJson = runtimeJson?.ToArray(),
            Status = unresolved ? BenchmarkJudgeAttemptStatus.Failed : BenchmarkJudgeAttemptStatus.Queued,
            ErrorMessage = failure,
            Variant = launchIntent?.Variant,
            KvCacheType = launchIntent?.KvCacheType,
            KvCacheTypeSource = launchIntent?.KvCacheTypeSource,
            KvAutoReason = launchIntent?.KvAutoReason,
            FlashAttentionMode = launchIntent?.FlashAttentionMode,
            IntendedLaunchIdentity = launchIntent?.IntendedLaunchIdentity,
            IntendedExecutableSha256 = launchIntent?.IntendedExecutableSha256,
            LaunchIdentityScheme = launchIntent?.LaunchIdentityScheme,
            EnqueuedAtUtc = now,
            CompletedAtUtc = unresolved ? now : null,
            Version = 1
        };
        _dbContext.BenchmarkJudgeAttempts.Add(attempt);
        _dbContext.BenchmarkWorkItems.Add(new BenchmarkWorkItem
        {
            RunId = run.Id,
            Kind = BenchmarkWorkKind.Judge,
            JudgeAttemptId = attempt.Id,
            Status = unresolved ? BenchmarkWorkStatus.Failed : BenchmarkWorkStatus.Queued,
            Attempt = 1,
            Version = 1,
            EnqueuedAtUtc = now,
            FinishedAtUtc = unresolved ? now : null,
            ErrorMessage = failure
        });
        run.CurrentJudgeAttemptId = attempt.Id;
        return attempt;
    }

    /// <summary>
    ///     Moves the attempt behind a judge work item to its terminal state and returns it. Never overwrites a terminal
    ///     one — a repeated terminalization returns <see langword="null" /> so no result is written twice.
    /// </summary>
    private async Task<BenchmarkJudgeAttempt?> TerminalizeJudgeAttemptAsync(BenchmarkWorkItem work,
        BenchmarkJudgeAttemptStatus status,
        string? errorMessage,
        long now,
        CancellationToken cancellationToken)
    {
        if (work.JudgeAttemptId is not { } attemptId)
        {
            return null;
        }

        var attempt = await _dbContext.BenchmarkJudgeAttempts.SingleOrDefaultAsync(entity => entity.Id == attemptId, cancellationToken);
        if (attempt is null
            || attempt.Status is BenchmarkJudgeAttemptStatus.Succeeded or BenchmarkJudgeAttemptStatus.Failed or BenchmarkJudgeAttemptStatus.Cancelled)
        {
            return null;
        }

        attempt.Status = status;
        attempt.ErrorMessage = errorMessage;
        attempt.CompletedAtUtc = now;
        attempt.Version++;
        return attempt;
    }

    /// <summary>
    ///     Moves a fidelity attempt to its terminal state and returns it, or <see langword="null" /> when there is
    ///     nothing to move.
    /// </summary>
    /// <remarks>
    ///     The run's fidelity projection is NOT touched here: a failed re-measurement must leave the numbers from the
    ///     last attempt that succeeded exactly where they are.
    /// </remarks>
    private async Task<BenchmarkFidelityAttempt?> TerminalizeFidelityAttemptAsync(Guid? attemptId,
        BenchmarkJudgeAttemptStatus status,
        string? errorMessage,
        long now,
        CancellationToken cancellationToken)
    {
        if (attemptId is not { } id)
        {
            return null;
        }

        var attempt = await _dbContext.BenchmarkFidelityAttempts.SingleOrDefaultAsync(entity => entity.Id == id, cancellationToken);
        if (attempt is null || IsAttemptTerminal(attempt.Status))
        {
            return null;
        }

        attempt.Status = status;
        attempt.ErrorMessage = errorMessage;
        attempt.CompletedAtUtc = now;
        attempt.Version++;
        return attempt;
    }

    /// <summary>Moves a pairwise comparison to its terminal state, never overwriting one that already reached one.</summary>
    private async Task<BenchmarkJudgeComparison?> TerminalizeComparisonAsync(Guid? comparisonId,
        BenchmarkJudgeAttemptStatus status,
        string? errorMessage,
        long now,
        CancellationToken cancellationToken)
    {
        if (comparisonId is not { } id)
        {
            return null;
        }

        var comparison = await _dbContext.BenchmarkComparisons.SingleOrDefaultAsync(entity => entity.Id == id, cancellationToken);
        if (comparison is null || IsAttemptTerminal(comparison.Status))
        {
            return null;
        }

        comparison.Status = status;
        comparison.ErrorMessage = errorMessage;
        comparison.CompletedAtUtc = now;
        comparison.Version++;

        // The cohort's comparison-set version moves in the SAME transaction as the terminalization. Inserting and terminalizing are the only two ways the fitted set
        // can change, so a published fit's staleness is one integer against this row rather than a re-hash of every verdict on every page read.
        var revision = await _dbContext.BenchmarkJudgePolicyRevisions
                                       .SingleOrDefaultAsync(entity => entity.Id == comparison.PolicyRevisionId, cancellationToken);
        if (revision is not null)
        {
            revision.ComparisonSetVersion = checked(revision.ComparisonSetVersion + 1);
        }

        return comparison;
    }

    private static bool IsAttemptTerminal(BenchmarkJudgeAttemptStatus status) =>
        status is BenchmarkJudgeAttemptStatus.Succeeded or BenchmarkJudgeAttemptStatus.Failed or BenchmarkJudgeAttemptStatus.Cancelled;

    /// <summary>
    ///     Get-or-create by <c>(project, hash)</c>: insert, and on the unique conflict re-query, so two racing
    ///     activations of the same policy converge on one row instead of minting a duplicate revision.
    /// </summary>
    private async Task<(BenchmarkJudgePolicyRevision Revision, bool WasCreated)> GetOrCreateJudgePolicyRevisionAsync(Guid projectId,
        ReadOnlyMemory<byte> policyJson,
        string policyHash,
        long now,
        CancellationToken cancellationToken)
    {
        var existing = await _dbContext.BenchmarkJudgePolicyRevisions
                                       .SingleOrDefaultAsync(entity => entity.ProjectId == projectId && entity.PolicyHash == policyHash, cancellationToken);
        if (existing is not null)
        {
            return (existing, false);
        }

        var lastRevision = await _dbContext.BenchmarkJudgePolicyRevisions
                                           .Where(entity => entity.ProjectId == projectId)
                                           .Select(entity => (int?)entity.Revision)
                                           .MaxAsync(cancellationToken)
                           ?? 0;
        var revision = new BenchmarkJudgePolicyRevision
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Revision = lastRevision + 1,
            PolicyJson = policyJson.ToArray(),
            PolicyHash = policyHash,
            ReferenceExecutionKey = null,
            CohortGeneration = 1,
            CreatedAtUtc = now
        };
        _dbContext.BenchmarkJudgePolicyRevisions.Add(revision);
        try
        {
            // Staged save inside the caller's transaction: the revision must exist before the project points at it,
            // and a failure anywhere after this still rolls both rows back together.
            await SaveAsync(cancellationToken);
        }
        catch (BenchmarkConflictException conflict) when (string.Equals(conflict.Code, "DuplicateWork", StringComparison.Ordinal))
        {
            _dbContext.Entry(revision).State = EntityState.Detached;
            var raced = await _dbContext.BenchmarkJudgePolicyRevisions
                                        .SingleOrDefaultAsync(entity => entity.ProjectId == projectId && entity.PolicyHash == policyHash, cancellationToken);
            if (raced is null)
            {
                throw;
            }

            return (raced, false);
        }

        return (revision, true);
    }

    /// <summary>
    ///     A run record carrying its derived judge view.
    /// </summary>
    /// <remarks>
    ///     Every path that returns a run uses this: the view is how a caller reads judge state now, and a record that
    ///     silently omitted it would read as "no judging" to a caller that had just terminalized one.
    /// </remarks>
}
