namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed partial class BenchmarkStore
{
    public async Task<BenchmarkFidelityAttemptRecord?> GetFidelityAttemptAsync(Guid attemptId, CancellationToken cancellationToken = default)
    {
        var attempt = await _dbContext.BenchmarkFidelityAttempts.AsNoTracking()
                                      .SingleOrDefaultAsync(entity => entity.Id == attemptId, cancellationToken)
                                      .ConfigureAwait(false);
        return attempt is null ? null : ToRecord(attempt);
    }

    public async Task<IReadOnlyList<BenchmarkFidelityAttemptRecord>> ListFidelityAttemptsAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var attempts = await _dbContext.BenchmarkFidelityAttempts.AsNoTracking()
                                       .Where(entity => entity.RunId == runId)
                                       .OrderByDescending(entity => entity.Sequence)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);
        return [.. attempts.Select(ToRecord)];
    }

    public async Task<IReadOnlySet<string>> ListLiveFidelityDigestsAsync(CancellationToken cancellationToken = default)
    {
        var digests = await _dbContext.BenchmarkFidelityAttempts.AsNoTracking()
                                      .Where(entity => (entity.Status == BenchmarkJudgeAttemptStatus.Queued || entity.Status == BenchmarkJudgeAttemptStatus.Running)
                                                       && entity.BaseLogitsDigest != null)
                                      .Select(entity => entity.BaseLogitsDigest!)
                                      .ToListAsync(cancellationToken)
                                      .ConfigureAwait(false);
        return digests.ToHashSet(StringComparer.Ordinal);
    }

    public Task<bool> HasLiveFidelityWorkAsync(CancellationToken cancellationToken = default) =>
        _dbContext.BenchmarkWorkItems.AsNoTracking()
                  .AnyAsync(entity => entity.Kind == BenchmarkWorkKind.Fidelity
                                      && (entity.Status == BenchmarkWorkStatus.Queued || entity.Status == BenchmarkWorkStatus.Running),
                      cancellationToken);

    public async Task<Guid> EnqueueFidelityAsync(Guid runId, string kind, CancellationToken cancellationToken = default)
    {
        if (kind is not (FidelityKindPerplexity or FidelityKindKld))
        {
            throw new BenchmarkValidationException("Benchmark fidelity kind must be 'ppl' or 'kld'.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var run = await RequireRunAsync(runId, tracking: true, cancellationToken).ConfigureAwait(false);
        if (await _dbContext.BenchmarkWorkItems.AnyAsync(entity => entity.RunId == runId
                                                                   && entity.Kind == BenchmarkWorkKind.Fidelity
                                                                   && (entity.Status == BenchmarkWorkStatus.Queued || entity.Status == BenchmarkWorkStatus.Running),
                                cancellationToken)
                            .ConfigureAwait(false))
        {
            throw new BenchmarkConflictException("FidelityAlreadyQueued");
        }

        var attempt = await AppendFidelityWorkAsync(run, kind, Now(), cancellationToken).ConfigureAwait(false);
        run.Version++;
        run.UpdatedAtUtc = Now();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return attempt.Id;
    }

    /// <summary>
    ///     Inserts the attempt-plus-work-item pair, WITHOUT owning a transaction, so freeze can append it inside the
    ///     one transaction that already inserts the run and its primary item.
    /// </summary>
    private async Task<BenchmarkFidelityAttempt> AppendFidelityWorkAsync(BenchmarkRun run, string kind, long now, CancellationToken cancellationToken)
    {
        var lastSequence = await _dbContext.BenchmarkFidelityAttempts
                                           .Where(entity => entity.RunId == run.Id)
                                           .MaxAsync(entity => (int?)entity.Sequence, cancellationToken)
                                           .ConfigureAwait(false)
                           ?? 0;
        var attempt = new BenchmarkFidelityAttempt
        {
            Id = Guid.NewGuid(),
            RunId = run.Id,
            Sequence = lastSequence + 1,
            Kind = kind,
            Status = BenchmarkJudgeAttemptStatus.Queued,
            EnqueuedAtUtc = now,
            Version = 1
        };
        _dbContext.BenchmarkFidelityAttempts.Add(attempt);
        _dbContext.BenchmarkWorkItems.Add(new BenchmarkWorkItem
        {
            RunId = run.Id,
            Kind = BenchmarkWorkKind.Fidelity,
            FidelityAttemptId = attempt.Id,
            Status = BenchmarkWorkStatus.Queued,
            Attempt = 1,
            Version = 1,
            EnqueuedAtUtc = now
        });
        run.FidelityStatus = "queued";
        run.FidelityErrorMessage = null;
        return attempt;
    }

    public Task<BenchmarkRunRecord> MarkFidelitySucceededAsync(BenchmarkFidelitySuccessCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return TerminalizeFidelityAsync(command.RunId,
            command.ExpectedWorkVersion,
            BenchmarkJudgeAttemptStatus.Succeeded,
            BenchmarkWorkStatus.Succeeded,
            errorMessage: null,
            command,
            cancellationToken);
    }

    public Task<BenchmarkRunRecord> MarkFidelityFailedAsync(Guid runId, long expectedWorkVersion, string errorMessage, CancellationToken cancellationToken = default) =>
        TerminalizeFidelityAsync(runId, expectedWorkVersion, BenchmarkJudgeAttemptStatus.Failed, BenchmarkWorkStatus.Failed, Sanitize(errorMessage),
            success: null, cancellationToken);

    public Task<BenchmarkRunRecord> MarkFidelityCancelledAsync(Guid runId, long expectedWorkVersion, CancellationToken cancellationToken = default) =>
        TerminalizeFidelityAsync(runId, expectedWorkVersion, BenchmarkJudgeAttemptStatus.Cancelled, BenchmarkWorkStatus.Cancelled, errorMessage: null,
            success: null, cancellationToken);

    public async Task<BenchmarkRunRecord> RequeueFidelityAsync(Guid runId,
        long expectedWorkVersion,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireWorkCompletionAsync(runId, BenchmarkWorkKind.Fidelity, expectedWorkVersion, cancellationToken).ConfigureAwait(false);
        _dbContext.ChangeTracker.Clear();
        var run = await RequireRunAsync(runId, tracking: true, cancellationToken).ConfigureAwait(false);
        var work = await RequireWorkAsync(run.Id, BenchmarkWorkKind.Fidelity, cancellationToken).ConfigureAwait(false);
        EnsureVersion(work.Version, expectedWorkVersion);
        var attempt = work.FidelityAttemptId is { } attemptId
            ? await _dbContext.BenchmarkFidelityAttempts.SingleOrDefaultAsync(entity => entity.Id == attemptId, cancellationToken).ConfigureAwait(false)
            : null;
        if (attempt is null || IsAttemptTerminal(attempt.Status))
        {
            // Something already finished this measurement. Re-queueing it would run a second one nobody asked for.
            return ToRecord(run);
        }

        // The CHECK pins attempt = 1, so a requeue is a status reset and not a retry counter: the work item goes back
        // to the head of its own queue slot and the consumer picks it up again on the next claim.
        var now = Now();
        var sanitized = Sanitize(reason);
        work.Status = BenchmarkWorkStatus.Queued;
        work.StartedAtUtc = null;
        work.ErrorMessage = sanitized;
        work.Version++;
        attempt.Status = BenchmarkJudgeAttemptStatus.Queued;
        attempt.StartedAtUtc = null;
        attempt.ErrorMessage = sanitized;
        attempt.Version++;

        // Still 'queued' rather than 'failed', with the reason beside it: the difference is exactly what a reader
        // needs to tell "this measurement is waiting on another process" from "this measurement will not happen".
        run.FidelityStatus = "queued";
        run.FidelityErrorMessage = sanitized;
        run.Version++;
        run.LastStreamSequence = checked(run.LastStreamSequence + 1);
        run.UpdatedAtUtc = now;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(run);
    }

    private async Task<BenchmarkRunRecord> TerminalizeFidelityAsync(Guid runId,
        long expectedWorkVersion,
        BenchmarkJudgeAttemptStatus attemptStatus,
        BenchmarkWorkStatus workStatus,
        string? errorMessage,
        BenchmarkFidelitySuccessCommand? success,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await AcquireWorkCompletionAsync(runId, BenchmarkWorkKind.Fidelity, expectedWorkVersion, cancellationToken).ConfigureAwait(false);
        _dbContext.ChangeTracker.Clear();
        var run = await RequireRunAsync(runId, tracking: true, cancellationToken).ConfigureAwait(false);
        var work = await RequireWorkAsync(run.Id, BenchmarkWorkKind.Fidelity, cancellationToken).ConfigureAwait(false);
        EnsureVersion(work.Version, expectedWorkVersion);
        var now = Now();
        TerminalizeWork(work, workStatus, errorMessage, now);
        var attempt = await TerminalizeFidelityAttemptAsync(work.FidelityAttemptId, attemptStatus, errorMessage, now, cancellationToken).ConfigureAwait(false);
        if (attempt is null)
        {
            // Already terminal: repeating a terminalization must not write a second measurement.
            return ToRecord(run);
        }

        if (success is not null)
        {
            attempt.PerplexityMean = success.PerplexityMean;
            attempt.PerplexityStdErr = success.PerplexityStdErr;
            attempt.PerplexityChunks = success.PerplexityChunks;
            attempt.PerplexityContextTokens = success.PerplexityContextTokens;
            attempt.CorpusId = success.CorpusId;
            attempt.KldMean = success.KldMean;
            attempt.KldP99 = success.KldP99;
            attempt.TopTokenAgreement = success.TopTokenAgreement;
            attempt.BaseModelName = success.BaseModelName;
            attempt.BaseModelContentFingerprint = success.BaseModelContentFingerprint;
            attempt.BaseLogitsDigest = success.BaseLogitsDigest;
            attempt.ReceiptJson = success.ReceiptJson.IsEmpty ? null : success.ReceiptJson.ToArray();
        }

        run.FidelityStatus = ToFidelityStatus(attemptStatus);
        run.FidelityErrorMessage = errorMessage;
        if (attemptStatus == BenchmarkJudgeAttemptStatus.Succeeded && await IsLatestSucceededFidelityAsync(attempt, cancellationToken).ConfigureAwait(false))
        {
            // The projection is a copy of the LATEST succeeded attempt. Guarding on the sequence rather than on
            // arrival order is what makes a re-measurement that lands out of order harmless instead of last-writer-wins.
            run.FidelityAttemptId = attempt.Id;
            run.PerplexityMean = attempt.PerplexityMean;
            run.PerplexityStdErr = attempt.PerplexityStdErr;
            run.PerplexityChunks = attempt.PerplexityChunks;
            run.PerplexityContextTokens = attempt.PerplexityContextTokens;
            run.PerplexityCorpusId = attempt.CorpusId;
            run.KldMean = attempt.KldMean;
            run.KldP99 = attempt.KldP99;
            run.TopTokenAgreement = attempt.TopTokenAgreement;
            run.KldBaseFingerprint = attempt.BaseModelContentFingerprint;
            run.KldBaseLogitsDigest = attempt.BaseLogitsDigest;
        }

        run.Version++;
        run.LastStreamSequence = checked(run.LastStreamSequence + 1);
        run.UpdatedAtUtc = now;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(run);
    }

    private async Task<bool> IsLatestSucceededFidelityAsync(BenchmarkFidelityAttempt attempt, CancellationToken cancellationToken)
    {
        var highest = await _dbContext.BenchmarkFidelityAttempts
                                      .Where(entity => entity.RunId == attempt.RunId
                                                       && entity.Status == BenchmarkJudgeAttemptStatus.Succeeded
                                                       && entity.Id != attempt.Id)
                                      .MaxAsync(entity => (int?)entity.Sequence, cancellationToken)
                                      .ConfigureAwait(false);
        return highest is null || attempt.Sequence > highest;
    }

    private static string ToFidelityStatus(BenchmarkJudgeAttemptStatus status) =>
        status switch
        {
            BenchmarkJudgeAttemptStatus.Succeeded => "succeeded",
            BenchmarkJudgeAttemptStatus.Failed => "failed",
            BenchmarkJudgeAttemptStatus.Cancelled => "cancelled",
            BenchmarkJudgeAttemptStatus.Running => "running",
            _ => "queued"
        };
}
