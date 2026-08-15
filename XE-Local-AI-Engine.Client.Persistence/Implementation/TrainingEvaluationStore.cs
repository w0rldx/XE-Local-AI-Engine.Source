namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     <see cref="ITrainingEvaluationStore" /> over <see cref="NodeChatDbContext" />. Enqueues onto the SAME
///     <c>training_work_items</c> queue <see cref="TrainingRunStore" /> owns, under
///     <see cref="TrainingWorkKind.EvaluationRun" /> — the claim, the startup recovery and the FIFO ordering therefore
///     stay in one place, and an evaluation can never run beside a training run.
/// </summary>
public sealed class TrainingEvaluationStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : ITrainingEvaluationStore
{
    /// <summary>Matches the <c>error_message</c> column's declared max length.</summary>
    private const int MaxErrorMessageLength = 1024;

    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<TrainingEvaluationRecord> CreateAndEnqueueAsync(TrainingEvaluationEnqueueCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.MembershipJson.IsEmpty)
        {
            throw new TrainingValidationException("An evaluation run requires a frozen hold-out membership.");
        }

        if (command.TotalCount <= 0)
        {
            throw new TrainingValidationException("An evaluation run requires at least one hold-out sample.");
        }

        if (string.IsNullOrWhiteSpace(command.ModelName))
        {
            throw new TrainingValidationException("An evaluation run requires the model it scores.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        _ = await _dbContext.TrainingDatasets.AsNoTracking()
                            .FirstOrDefaultAsync(item => item.Id == command.DatasetId, cancellationToken)
                            .ConfigureAwait(false)
            ?? throw new TrainingNotFoundException("The training dataset was not found.");
        if (command.TrainingRunId is { } runId
            && !await _dbContext.TrainingRuns.AnyAsync(item => item.Id == runId, cancellationToken).ConfigureAwait(false))
        {
            throw new TrainingNotFoundException("The training run was not found.");
        }

        var now = Now();
        var evaluation = new TrainingEvaluationRun
        {
            Id = Guid.NewGuid(),
            TrainingRunId = command.TrainingRunId,
            ModelName = command.ModelName,
            ModelContentFingerprint = command.ModelContentFingerprint,
            DatasetId = command.DatasetId,
            DatasetContentFingerprint = command.DatasetContentFingerprint,
            MembershipJson = command.MembershipJson.ToArray(),
            Status = TrainingEvaluationStatus.Queued,
            TotalCount = command.TotalCount,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _ = _dbContext.TrainingEvaluationRuns.Add(evaluation);
        _ = _dbContext.TrainingWorkItems.Add(new TrainingWorkItem
        {
            Kind = TrainingWorkKind.EvaluationRun,
            TargetId = evaluation.Id,
            Status = TrainingWorkStatus.Queued,
            Attempt = 1,
            Version = 1,
            EnqueuedAtUtc = now
        });

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(evaluation, TrainingWorkStatus.Queued);
    }

    public async Task<TrainingEvaluationRecord?> GetAsync(Guid evaluationId, CancellationToken cancellationToken = default)
    {
        var evaluation = await _dbContext.TrainingEvaluationRuns.AsNoTracking()
                                         .FirstOrDefaultAsync(item => item.Id == evaluationId, cancellationToken)
                                         .ConfigureAwait(false);
        if (evaluation is null)
        {
            return null;
        }

        var work = await FindWorkAsync(evaluationId, tracking: false, cancellationToken).ConfigureAwait(false);
        return ToRecord(evaluation, work?.Status);
    }

    public async Task<IReadOnlyList<TrainingEvaluationRecord>> ListAsync(Guid? trainingRunId, CancellationToken cancellationToken = default)
    {
        var filtered = _dbContext.TrainingEvaluationRuns.AsNoTracking();
        if (trainingRunId is { } runId)
        {
            filtered = filtered.Where(item => item.TrainingRunId == runId);
        }

        var evaluations = await filtered.OrderByDescending(item => item.CreatedAtUtc)
                                        // Secondary key so two evaluations created in the same millisecond keep a stable order.
                                        .ThenBy(item => item.Id)
                                        .ToListAsync(cancellationToken)
                                        .ConfigureAwait(false);
        var ids = evaluations.Select(item => item.Id).ToList();
        var work = await _dbContext.TrainingWorkItems.AsNoTracking()
                                   .Where(item => item.Kind == TrainingWorkKind.EvaluationRun && ids.Contains(item.TargetId))
                                   .ToDictionaryAsync(item => item.TargetId, cancellationToken)
                                   .ConfigureAwait(false);
        return evaluations.Select(item => ToRecord(item, work.TryGetValue(item.Id, out var found) ? found.Status : null)).ToArray();
    }

    public async Task<TrainingEvaluationRecord> AppendResultsAsync(Guid evaluationId,
        IReadOnlyList<TrainingEvaluationResultEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var evaluation = await RequireAsync(evaluationId, tracking: true, cancellationToken).ConfigureAwait(false);
        var merged = TrainingEvaluationResults.Read(OptionalBlob.AsOptionalMemory(evaluation.ResultsJson)).ToList();
        var scored = new HashSet<Guid>(merged.Select(entry => entry.SampleId));
        var added = false;
        foreach (var entry in entries)
        {
            // Idempotent by sample id: a resume that re-offers a verdict the previous attempt already wrote changes
            // nothing, so re-entering the loop after an interruption cannot double-count a sample.
            if (!scored.Add(entry.SampleId))
            {
                continue;
            }

            merged.Add(entry);
            added = true;
        }

        if (!added)
        {
            var unchangedWork = await FindWorkAsync(evaluationId, tracking: false, cancellationToken).ConfigureAwait(false);
            return ToRecord(evaluation, unchangedWork?.Status);
        }

        var tally = TrainingEvaluationResults.Tally(merged);
        evaluation.ResultsJson = TrainingEvaluationResults.Write(merged);
        // Every aggregate is recomputed from the merged set rather than incremented: the blob is the authority, and a
        // counter that drifts from it would make the comparison report unreproducible.
        evaluation.ScoredCount = merged.Count;
        evaluation.PassedCount = merged.Count(entry => entry.Passed);
        evaluation.PerKindJson = TrainingEvaluationResults.WriteTally(tally);
        evaluation.UpdatedAtUtc = Now();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        var work = await FindWorkAsync(evaluationId, tracking: false, cancellationToken).ConfigureAwait(false);
        return ToRecord(evaluation, work?.Status);
    }

    public async Task<TrainingEvaluationRecord> TransitionAsync(Guid evaluationId,
        long expectedVersion,
        TrainingEvaluationStatus status,
        CancellationToken cancellationToken = default)
    {
        if (status is TrainingEvaluationStatus.Queued || IsTerminal(status))
        {
            throw new TrainingValidationException("Only the non-terminal progression is written here; terminal statuses go through CompleteAsync.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var evaluation = await RequireAsync(evaluationId, tracking: true, cancellationToken).ConfigureAwait(false);
        EnsureVersion(evaluation.Version, expectedVersion);
        if (IsTerminal(evaluation.Status))
        {
            throw new TrainingConflictException("EvaluationTerminal");
        }

        evaluation.Status = status;
        evaluation.Version++;
        evaluation.UpdatedAtUtc = Now();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var work = await FindWorkAsync(evaluationId, tracking: false, cancellationToken).ConfigureAwait(false);
        return ToRecord(evaluation, work?.Status);
    }

    public async Task<TrainingEvaluationRecord> CompleteAsync(Guid evaluationId,
        TrainingWorkStatus status,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        if (status is TrainingWorkStatus.Queued or TrainingWorkStatus.Running)
        {
            throw new TrainingValidationException("An evaluation run can only be completed into a terminal status.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var evaluation = await RequireAsync(evaluationId, tracking: true, cancellationToken).ConfigureAwait(false);
        var work = await FindWorkAsync(evaluationId, tracking: true, cancellationToken).ConfigureAwait(false)
                   ?? throw new TrainingNotFoundException("The evaluation work item was not found.");
        if (IsTerminal(work.Status))
        {
            // Idempotent: a startup retrace or a double-terminalize is a silent no-op.
            return ToRecord(evaluation, work.Status);
        }

        var now = Now();
        work.Status = status;
        work.ErrorMessage = Sanitize(errorMessage);
        work.FinishedAtUtc = now;
        work.Version++;
        evaluation.Status = status switch
        {
            TrainingWorkStatus.Succeeded => TrainingEvaluationStatus.Succeeded,
            TrainingWorkStatus.Cancelled => TrainingEvaluationStatus.Cancelled,
            _ => TrainingEvaluationStatus.Failed
        };
        evaluation.ErrorMessage = Sanitize(errorMessage);
        evaluation.Version++;
        evaluation.UpdatedAtUtc = now;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(evaluation, work.Status);
    }

    public async Task<TrainingEvaluationRecord> ResumeAsync(Guid evaluationId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var evaluation = await RequireAsync(evaluationId, tracking: true, cancellationToken).ConfigureAwait(false);
        EnsureVersion(evaluation.Version, expectedVersion);
        if (!IsTerminal(evaluation.Status))
        {
            throw new TrainingConflictException("EvaluationActive");
        }

        if (evaluation.ScoredCount >= evaluation.TotalCount)
        {
            throw new TrainingConflictException("EvaluationComplete");
        }

        // ux_training_work_items_target_kind is unique, and the frozen queue semantics never retry a work item in
        // place, so the terminal row is deleted and replaced rather than reset.
        _ = await _dbContext.TrainingWorkItems
                            .Where(item => item.TargetId == evaluationId && item.Kind == TrainingWorkKind.EvaluationRun)
                            .ExecuteDeleteAsync(cancellationToken)
                            .ConfigureAwait(false);
        var now = Now();
        _ = _dbContext.TrainingWorkItems.Add(new TrainingWorkItem
        {
            Kind = TrainingWorkKind.EvaluationRun,
            TargetId = evaluationId,
            Status = TrainingWorkStatus.Queued,
            Attempt = 1,
            Version = 1,
            EnqueuedAtUtc = now
        });
        evaluation.Status = TrainingEvaluationStatus.Queued;
        evaluation.ErrorMessage = null;
        evaluation.Version++;
        evaluation.UpdatedAtUtc = now;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(evaluation, TrainingWorkStatus.Queued);
    }

    public async Task DeleteAsync(Guid evaluationId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var evaluation = await RequireAsync(evaluationId, tracking: true, cancellationToken).ConfigureAwait(false);
        EnsureVersion(evaluation.Version, expectedVersion);
        if (evaluation.ComparisonId is not null)
        {
            throw new TrainingConflictException("EvaluationBound");
        }

        if (await _dbContext.TrainingWorkItems
                            .AnyAsync(item => item.TargetId == evaluationId
                                              && item.Kind == TrainingWorkKind.EvaluationRun
                                              && (item.Status == TrainingWorkStatus.Queued || item.Status == TrainingWorkStatus.Running),
                                cancellationToken)
                            .ConfigureAwait(false))
        {
            throw new TrainingConflictException("EvaluationActive");
        }

        // Explicit ordered deletes: nothing cascades on the node connection. Work item first, then the evaluation.
        _ = await _dbContext.TrainingWorkItems
                            .Where(item => item.TargetId == evaluationId && item.Kind == TrainingWorkKind.EvaluationRun)
                            .ExecuteDeleteAsync(cancellationToken)
                            .ConfigureAwait(false);
        _dbContext.ChangeTracker.Clear();
        _ = await _dbContext.TrainingEvaluationRuns.Where(item => item.Id == evaluationId)
                            .ExecuteDeleteAsync(cancellationToken)
                            .ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TrainingComparisonRecord> CreateComparisonAsync(TrainingComparisonInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw new TrainingValidationException("A comparison report requires a name.");
        }

        if (input.DeltasJson.IsEmpty)
        {
            throw new TrainingValidationException("A comparison report requires computed deltas.");
        }

        if (input.BaseEvaluationRunId == input.TunedEvaluationRunId)
        {
            throw new TrainingValidationException("A comparison report needs two distinct evaluation runs.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var baseEvaluation = await RequireAsync(input.BaseEvaluationRunId, tracking: true, cancellationToken).ConfigureAwait(false);
        var tunedEvaluation = await RequireAsync(input.TunedEvaluationRunId, tracking: true, cancellationToken).ConfigureAwait(false);
        if (baseEvaluation.ComparisonId is not null || tunedEvaluation.ComparisonId is not null)
        {
            throw new TrainingConflictException("EvaluationBound");
        }

        var now = Now();
        var report = new TrainingComparisonReport
        {
            Id = Guid.NewGuid(),
            Name = input.Name,
            BaseEvaluationRunId = baseEvaluation.Id,
            TunedEvaluationRunId = tunedEvaluation.Id,
            BaseBenchmarkRunId = input.BaseBenchmarkRunId,
            TunedBenchmarkRunId = input.TunedBenchmarkRunId,
            TrainingRunId = input.TrainingRunId,
            DeltasJson = input.DeltasJson.ToArray(),
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _ = _dbContext.TrainingComparisonReports.Add(report);
        Bind(baseEvaluation, report.Id, now);
        Bind(tunedEvaluation, report.Id, now);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(report);
    }

    public async Task<TrainingComparisonRecord?> GetComparisonAsync(Guid comparisonId, CancellationToken cancellationToken = default)
    {
        var report = await _dbContext.TrainingComparisonReports.AsNoTracking()
                                     .FirstOrDefaultAsync(item => item.Id == comparisonId, cancellationToken)
                                     .ConfigureAwait(false);
        return report is null ? null : ToRecord(report);
    }

    public async Task<IReadOnlyList<TrainingComparisonRecord>> ListComparisonsAsync(CancellationToken cancellationToken = default)
    {
        var reports = await _dbContext.TrainingComparisonReports.AsNoTracking()
                                      .OrderByDescending(item => item.CreatedAtUtc)
                                      .ThenBy(item => item.Id)
                                      .ToListAsync(cancellationToken)
                                      .ConfigureAwait(false);
        return reports.Select(ToRecord).ToArray();
    }

    public async Task DeleteComparisonAsync(Guid comparisonId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var report = await _dbContext.TrainingComparisonReports.FirstOrDefaultAsync(item => item.Id == comparisonId, cancellationToken)
                                     .ConfigureAwait(false)
                     ?? throw new TrainingNotFoundException("The comparison report was not found.");
        EnsureVersion(report.Version, expectedVersion);

        // Unbind first: an evaluation still pointing at a deleted report would be undeletable forever.
        var now = Now();
        var bound = await _dbContext.TrainingEvaluationRuns.Where(item => item.ComparisonId == comparisonId)
                                    .ToListAsync(cancellationToken)
                                    .ConfigureAwait(false);
        foreach (var evaluation in bound)
        {
            evaluation.ComparisonId = null;
            evaluation.Version++;
            evaluation.UpdatedAtUtc = now;
        }

        _ = _dbContext.TrainingComparisonReports.Remove(report);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Bind(TrainingEvaluationRun evaluation, Guid comparisonId, long now)
    {
        evaluation.ComparisonId = comparisonId;
        evaluation.Version++;
        evaluation.UpdatedAtUtc = now;
    }

    private static bool IsTerminal(TrainingEvaluationStatus status) =>
        status is TrainingEvaluationStatus.Succeeded or TrainingEvaluationStatus.Failed or TrainingEvaluationStatus.Cancelled;

    private static bool IsTerminal(TrainingWorkStatus status) =>
        status is TrainingWorkStatus.Succeeded or TrainingWorkStatus.Failed or TrainingWorkStatus.Cancelled;

    private static string? Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        return message.Length > MaxErrorMessageLength ? message[..MaxErrorMessageLength] : message;
    }

    private static void EnsureVersion(long actual, long expected)
    {
        if (actual != expected)
        {
            throw new TrainingConflictException("VersionConflict");
        }
    }

    private async Task<TrainingEvaluationRun> RequireAsync(Guid evaluationId, bool tracking, CancellationToken cancellationToken)
    {
        var query = tracking ? _dbContext.TrainingEvaluationRuns : _dbContext.TrainingEvaluationRuns.AsNoTracking();
        return await query.FirstOrDefaultAsync(item => item.Id == evaluationId, cancellationToken).ConfigureAwait(false)
               ?? throw new TrainingNotFoundException("The evaluation run was not found.");
    }

    private async Task<TrainingWorkItem?> FindWorkAsync(Guid evaluationId, bool tracking, CancellationToken cancellationToken)
    {
        var query = tracking ? _dbContext.TrainingWorkItems : _dbContext.TrainingWorkItems.AsNoTracking();
        return await query.FirstOrDefaultAsync(item => item.TargetId == evaluationId && item.Kind == TrainingWorkKind.EvaluationRun, cancellationToken)
                          .ConfigureAwait(false);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            _ = await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw new TrainingConflictException("VersionConflict")
            {
                Source = exception.Source
            };
        }
        catch (DbUpdateException exception) when (exception.InnerException?.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase) == true)
        {
            throw new TrainingConflictException("DuplicateWork")
            {
                Source = exception.Source
            };
        }
    }

    private long Now() =>
        _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

    private static TrainingEvaluationRecord ToRecord(TrainingEvaluationRun entity, TrainingWorkStatus? workStatus) =>
        new(entity.Id, entity.TrainingRunId, entity.ComparisonId, entity.ModelName, entity.ModelContentFingerprint, entity.DatasetId,
            entity.DatasetContentFingerprint, entity.MembershipJson.ToArray(), entity.Status,
            OptionalBlob.AsOptionalMemory(entity.ResultsJson), entity.TotalCount, entity.ScoredCount, entity.PassedCount,
            entity.PerKindJson, entity.ErrorMessage, entity.Version, entity.CreatedAtUtc, entity.UpdatedAtUtc, workStatus);

    private static TrainingComparisonRecord ToRecord(TrainingComparisonReport entity) =>
        new(entity.Id, entity.Name, entity.BaseEvaluationRunId, entity.TunedEvaluationRunId, entity.BaseBenchmarkRunId,
            entity.TunedBenchmarkRunId, entity.TrainingRunId, entity.DeltasJson.ToArray(), entity.Version, entity.CreatedAtUtc,
            entity.UpdatedAtUtc);
}
