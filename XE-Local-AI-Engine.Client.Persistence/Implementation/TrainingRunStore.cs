namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     <see cref="ITrainingRunStore" /> over <see cref="NodeChatDbContext" />. The queue half duplicates
///     <see cref="TrainingDatasetStore" />'s claim/terminalize shape rather than generalizing it — the third copy of
///     the benchmark pattern, and the first whose target is polymorphic over a work kind.
/// </summary>
public sealed class TrainingRunStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : ITrainingRunStore
{
    /// <summary>How much trainer output the tail keeps, in characters. Trimmed in chars, not bytes: a byte-wise trim
    ///     would split a multi-byte codepoint and the column would no longer decode.</summary>
    public const int MaxLogTailLength = 16384;

    /// <summary>Matches the <c>error_message</c> and <c>smoke_reason</c> columns' declared max length.</summary>
    private const int MaxErrorMessageLength = 1024;

    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<TrainingRunRecord> CreateAndEnqueueAsync(TrainingRunEnqueueCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.LicenseConfirmationJson.IsEmpty)
        {
            throw new TrainingValidationException("A training run requires a recorded license confirmation.");
        }

        if (command.FreezeJson.IsEmpty || command.OptionsJson.IsEmpty)
        {
            throw new TrainingValidationException("A training run requires both a freeze and resolved options.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var dataset = await _dbContext.TrainingDatasets.AsNoTracking()
                                      .FirstOrDefaultAsync(item => item.Id == command.DatasetId, cancellationToken)
                                      .ConfigureAwait(false)
                      ?? throw new TrainingNotFoundException("The training dataset was not found.");
        EnsureVersion(dataset.Version, command.ExpectedDatasetVersion);
        if (dataset.Status != TrainingDatasetStatus.Ready || dataset.ContentFingerprint is null)
        {
            throw new TrainingConflictException("DatasetNotReady");
        }

        var baseArtifact = await _dbContext.TrainingBaseArtifacts.AsNoTracking()
                                           .FirstOrDefaultAsync(item => item.Id == command.BaseArtifactId, cancellationToken)
                                           .ConfigureAwait(false)
                           ?? throw new TrainingNotFoundException("The training base artifact was not found.");
        if (baseArtifact.Status != TrainingBaseArtifactStatus.Ready)
        {
            throw new TrainingConflictException("BaseArtifactNotReady");
        }

        var now = Now();
        var run = new TrainingRun
        {
            Id = Guid.NewGuid(),
            DatasetId = dataset.Id,
            // Read and copied inside this transaction: the copy IS the freeze, so a concurrent sample edit cannot slip
            // between the read and the insert and leave the run pointing at a membership it never saw.
            DatasetContentFingerprint = dataset.ContentFingerprint,
            DatasetRevision = dataset.Revision,
            FreezeJson = command.FreezeJson.ToArray(),
            BaseArtifactId = baseArtifact.Id,
            LinkedInstalledModelName = command.LinkedInstalledModelName,
            LinkedModelContentFingerprint = command.LinkedModelContentFingerprint,
            OptionsJson = command.OptionsJson.ToArray(),
            LicenseConfirmationJson = command.LicenseConfirmationJson.ToArray(),
            Status = TrainingRunStatus.Queued,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _ = _dbContext.TrainingRuns.Add(run);
        _ = _dbContext.TrainingWorkItems.Add(new TrainingWorkItem
        {
            Kind = TrainingWorkKind.TrainingRun,
            TargetId = run.Id,
            Status = TrainingWorkStatus.Queued,
            Attempt = 1,
            Version = 1,
            EnqueuedAtUtc = now
        });

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(run, TrainingWorkStatus.Queued, workErrorMessage: null);
    }

    public async Task<TrainingRunRecord?> GetAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await _dbContext.TrainingRuns.AsNoTracking()
                                  .FirstOrDefaultAsync(item => item.Id == runId, cancellationToken)
                                  .ConfigureAwait(false);
        if (run is null)
        {
            return null;
        }

        var work = await FindWorkAsync(runId, tracking: false, cancellationToken).ConfigureAwait(false);
        return ToRecord(run, work?.Status, work?.ErrorMessage);
    }

    public async Task<TrainingRunPage> ListAsync(TrainingRunQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Page < 1 || query.PageSize is < 1 or > 200)
        {
            throw new TrainingValidationException("Page must be positive and pageSize must be between 1 and 200.");
        }

        var filtered = _dbContext.TrainingRuns.AsNoTracking();
        if (query.DatasetId is { } datasetId)
        {
            filtered = filtered.Where(item => item.DatasetId == datasetId);
        }

        if (query.Status is { } status)
        {
            filtered = filtered.Where(item => item.Status == status);
        }

        var total = await filtered.CountAsync(cancellationToken).ConfigureAwait(false);
        var runs = await filtered.OrderByDescending(item => item.CreatedAtUtc)
                                 // Secondary key so two runs created in the same millisecond cannot swap pages.
                                 .ThenBy(item => item.Id)
                                 .Skip((query.Page - 1) * query.PageSize)
                                 .Take(query.PageSize)
                                 .ToListAsync(cancellationToken)
                                 .ConfigureAwait(false);
        var runIds = runs.Select(item => item.Id).ToList();
        var work = await _dbContext.TrainingWorkItems.AsNoTracking()
                                   .Where(item => item.Kind == TrainingWorkKind.TrainingRun && runIds.Contains(item.TargetId))
                                   .ToDictionaryAsync(item => item.TargetId, cancellationToken)
                                   .ConfigureAwait(false);
        var items = runs.Select(run => ToRecord(run,
                            work.TryGetValue(run.Id, out var found) ? found.Status : null,
                            work.TryGetValue(run.Id, out var byId) ? byId.ErrorMessage : null))
                        .ToArray();
        return new TrainingRunPage(items, total);
    }

    public async Task<TrainingWorkClaim?> ClaimNextAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var candidate = await _dbContext.TrainingWorkItems.AsNoTracking()
                                            .Where(item => item.Status == TrainingWorkStatus.Queued)
                                            .OrderBy(item => item.QueueSequence)
                                            .Select(item => new
                                            {
                                                item.QueueSequence,
                                                item.Version
                                            })
                                            .FirstOrDefaultAsync(cancellationToken)
                                            .ConfigureAwait(false);
            if (candidate is null)
            {
                return null;
            }

            var now = Now();
            var nextVersion = candidate.Version + 1;
            var claimed = await _dbContext.TrainingWorkItems
                                          .Where(item => item.QueueSequence == candidate.QueueSequence
                                                         && item.Version == candidate.Version
                                                         && item.Status == TrainingWorkStatus.Queued)
                                          .ExecuteUpdateAsync(setters => setters
                                                                         .SetProperty(item => item.Status, TrainingWorkStatus.Running)
                                                                         .SetProperty(item => item.StartedAtUtc, now)
                                                                         .SetProperty(item => item.Version, nextVersion),
                                              cancellationToken)
                                          .ConfigureAwait(false);
            if (claimed == 0)
            {
                // Another consumer won the compare-and-swap; retry against the next queued row.
                continue;
            }

            _dbContext.ChangeTracker.Clear();
            var work = await _dbContext.TrainingWorkItems.AsNoTracking()
                                       .SingleAsync(item => item.QueueSequence == candidate.QueueSequence, cancellationToken)
                                       .ConfigureAwait(false);
            var run = work.Kind == TrainingWorkKind.TrainingRun
                ? await GetAsync(work.TargetId, cancellationToken).ConfigureAwait(false)
                : null;
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new TrainingWorkClaim(work.QueueSequence, work.Kind, work.TargetId, work.Version, run);
        }
    }

    public async Task<IReadOnlyList<Guid>> RecoverOnStartupAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var interrupted = await _dbContext.TrainingWorkItems
                                          .Where(item => item.Status == TrainingWorkStatus.Running)
                                          .ToListAsync(cancellationToken)
                                          .ConfigureAwait(false);
        var now = Now();
        var recovered = new List<Guid>(interrupted.Count);
        foreach (var work in interrupted)
        {
            TerminalizeWork(work, TrainingWorkStatus.Failed, "The training run was interrupted by a host restart.", now);
            if (work.Kind == TrainingWorkKind.TrainingRun)
            {
                var run = await _dbContext.TrainingRuns.FirstOrDefaultAsync(item => item.Id == work.TargetId, cancellationToken).ConfigureAwait(false);
                if (run is not null && !IsTerminal(run.Status))
                {
                    run.Status = TrainingRunStatus.Failed;
                    run.ErrorMessage = "The training run was interrupted by a host restart.";
                    // The receipt described a process that no longer exists; leaving it would let a reaper act on a
                    // recycled PID.
                    run.LaunchReceiptJson = null;
                    run.Version++;
                    run.UpdatedAtUtc = now;
                }
            }

            recovered.Add(work.TargetId);
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return recovered;
    }

    public async Task<TrainingRunRecord> TransitionAsync(Guid runId,
        long expectedVersion,
        TrainingRunStatus status,
        CancellationToken cancellationToken = default)
    {
        if (status == TrainingRunStatus.Queued || IsTerminal(status))
        {
            throw new TrainingValidationException("Only the non-terminal progression is written here; terminal statuses go through CompleteRunAsync.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var run = await RequireRunAsync(runId, tracking: true, cancellationToken).ConfigureAwait(false);
        EnsureVersion(run.Version, expectedVersion);
        if (IsTerminal(run.Status))
        {
            throw new TrainingConflictException("RunTerminal");
        }

        run.Status = status;
        run.Version++;
        run.UpdatedAtUtc = Now();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var work = await FindWorkAsync(runId, tracking: false, cancellationToken).ConfigureAwait(false);
        return ToRecord(run, work?.Status, work?.ErrorMessage);
    }

    public async Task<TrainingRunRecord> CompleteRunAsync(Guid runId,
        TrainingWorkStatus status,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        if (status is TrainingWorkStatus.Queued or TrainingWorkStatus.Running)
        {
            throw new TrainingValidationException("A training run can only be completed into a terminal status.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var run = await RequireRunAsync(runId, tracking: true, cancellationToken).ConfigureAwait(false);
        var work = await FindWorkAsync(runId, tracking: true, cancellationToken).ConfigureAwait(false)
                   ?? throw new TrainingNotFoundException("The training work item was not found.");
        if (IsTerminal(work.Status))
        {
            // Idempotent: a startup retrace or a double-terminalize is a silent no-op.
            return ToRecord(run, work.Status, work.ErrorMessage);
        }

        var now = Now();
        TerminalizeWork(work, status, errorMessage, now);
        run.Status = status switch
        {
            TrainingWorkStatus.Succeeded => TrainingRunStatus.Succeeded,
            TrainingWorkStatus.Cancelled => TrainingRunStatus.Cancelled,
            _ => TrainingRunStatus.Failed
        };
        run.ErrorMessage = Sanitize(errorMessage);
        // The trainer process is gone by the time a run terminalizes; a stale receipt would point the reaper at a PID
        // the operating system is free to have reused.
        run.LaunchReceiptJson = null;
        run.Version++;
        run.UpdatedAtUtc = now;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(run, work.Status, work.ErrorMessage);
    }

    public async Task UpdateProgressAsync(Guid runId, ReadOnlyMemory<byte> progressJson, CancellationToken cancellationToken = default)
    {
        var run = await RequireRunAsync(runId, tracking: true, cancellationToken).ConfigureAwait(false);
        run.ProgressJson = progressJson.ToArray();
        run.UpdatedAtUtc = Now();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AppendLogTailAsync(Guid runId, string chunk, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return;
        }

        var run = await RequireRunAsync(runId, tracking: true, cancellationToken).ConfigureAwait(false);
        var existing = run.LogTail is null ? string.Empty : Encoding.UTF8.GetString(run.LogTail);
        var combined = existing + chunk;
        if (combined.Length > MaxLogTailLength)
        {
            combined = combined[^MaxLogTailLength..];
        }

        run.LogTail = Encoding.UTF8.GetBytes(combined);
        run.UpdatedAtUtc = Now();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetLaunchReceiptAsync(Guid runId, ReadOnlyMemory<byte>? launchReceiptJson, CancellationToken cancellationToken = default)
    {
        var run = await RequireRunAsync(runId, tracking: true, cancellationToken).ConfigureAwait(false);
        run.LaunchReceiptJson = launchReceiptJson?.ToArray();
        run.UpdatedAtUtc = Now();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid runId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var run = await RequireRunAsync(runId, tracking: true, cancellationToken).ConfigureAwait(false);
        EnsureVersion(run.Version, expectedVersion);
        if (await _dbContext.TrainingWorkItems
                            .AnyAsync(item => item.TargetId == runId
                                              && (item.Status == TrainingWorkStatus.Queued || item.Status == TrainingWorkStatus.Running),
                                cancellationToken)
                            .ConfigureAwait(false))
        {
            throw new TrainingConflictException("RunActive");
        }

        if (await _dbContext.TrainingArtifacts
                            .AnyAsync(item => item.RunId == runId && item.CommittedModelName != null, cancellationToken)
                            .ConfigureAwait(false))
        {
            // A promoted artifact is a registry entry with its own lifecycle; deleting the run would orphan its lineage.
            throw new TrainingConflictException("ArtifactPromoted");
        }

        // Explicit ordered deletes: the node connection never sets PRAGMA foreign_keys=ON, so the declared restrict on
        // training_artifacts never fires. Children first, then the work item, then the run itself.
        _ = await _dbContext.TrainingArtifacts.Where(item => item.RunId == runId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        _ = await _dbContext.TrainingWorkItems.Where(item => item.TargetId == runId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        // ExecuteDelete bypasses the tracker; clearing it stops EF reading the removed children as a severed required
        // association when the parent row is deleted (the dataset delete precedent).
        _dbContext.ChangeTracker.Clear();
        _ = await _dbContext.TrainingRuns.Where(item => item.Id == runId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TrainingArtifactRecord> CreateArtifactAsync(TrainingArtifactInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.Path))
        {
            throw new TrainingValidationException("A training artifact requires a staged path.");
        }

        _ = await RequireRunAsync(input.RunId, tracking: false, cancellationToken).ConfigureAwait(false);
        var now = Now();
        var artifact = new TrainingArtifact
        {
            Id = Guid.NewGuid(),
            RunId = input.RunId,
            Kind = input.Kind,
            Path = input.Path,
            SmokeState = TrainingArtifactSmokeState.Pending,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _ = _dbContext.TrainingArtifacts.Add(artifact);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(artifact);
    }

    public async Task<IReadOnlyList<TrainingArtifactRecord>> ListArtifactsAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var artifacts = await _dbContext.TrainingArtifacts.AsNoTracking()
                                        .Where(item => item.RunId == runId)
                                        .OrderBy(item => item.CreatedAtUtc)
                                        .ThenBy(item => item.Id)
                                        .ToListAsync(cancellationToken)
                                        .ConfigureAwait(false);
        return artifacts.Select(ToRecord).ToArray();
    }

    public async Task<TrainingArtifactRecord?> GetArtifactAsync(Guid artifactId, CancellationToken cancellationToken = default)
    {
        var artifact = await _dbContext.TrainingArtifacts.AsNoTracking()
                                       .FirstOrDefaultAsync(item => item.Id == artifactId, cancellationToken)
                                       .ConfigureAwait(false);
        return artifact is null ? null : ToRecord(artifact);
    }

    public async Task<TrainingArtifactRecord> SetArtifactDigestAsync(Guid artifactId,
        long expectedVersion,
        string sha256,
        long sizeBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sha256);
        if (sizeBytes < 0)
        {
            throw new TrainingValidationException("A training artifact cannot have a negative size.");
        }

        var artifact = await RequireArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false);
        EnsureVersion(artifact.Version, expectedVersion);
        artifact.Sha256 = sha256;
        artifact.SizeBytes = sizeBytes;
        artifact.Version++;
        artifact.UpdatedAtUtc = Now();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(artifact);
    }

    public async Task<TrainingArtifactRecord> SetArtifactSmokeStateAsync(Guid artifactId,
        long expectedVersion,
        TrainingArtifactSmokeState state,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        if (state == TrainingArtifactSmokeState.Pending)
        {
            throw new TrainingValidationException("Smoke state only moves forward into a decided value.");
        }

        if (state != TrainingArtifactSmokeState.Passed && string.IsNullOrWhiteSpace(reason))
        {
            // A skip is an operator decision and a failure is a diagnosis; neither is allowed to be silent.
            throw new TrainingValidationException("A failed or skipped smoke test requires a reason.");
        }

        var artifact = await RequireArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false);
        EnsureVersion(artifact.Version, expectedVersion);
        artifact.SmokeState = state;
        artifact.SmokeReason = Sanitize(reason);
        artifact.Version++;
        artifact.UpdatedAtUtc = Now();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(artifact);
    }

    public async Task<TrainingArtifactRecord> SetArtifactCommittedNameAsync(Guid artifactId,
        long expectedVersion,
        string? committedModelName,
        CancellationToken cancellationToken = default)
    {
        if (committedModelName is not null && string.IsNullOrWhiteSpace(committedModelName))
        {
            throw new TrainingValidationException("A registry name is either absent or non-blank.");
        }

        var artifact = await RequireArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false);
        EnsureVersion(artifact.Version, expectedVersion);
        if (committedModelName is not null && artifact.SmokeState is TrainingArtifactSmokeState.Pending or TrainingArtifactSmokeState.Failed)
        {
            throw new TrainingConflictException("SmokeNotPassed");
        }

        artifact.CommittedModelName = committedModelName;
        artifact.Version++;
        artifact.UpdatedAtUtc = Now();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(artifact);
    }

    public async Task DeleteArtifactAsync(Guid artifactId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        var artifact = await RequireArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false);
        EnsureVersion(artifact.Version, expectedVersion);
        if (artifact.CommittedModelName is not null)
        {
            throw new TrainingConflictException("ArtifactPromoted");
        }

        _ = _dbContext.TrainingArtifacts.Remove(artifact);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void TerminalizeWork(TrainingWorkItem work, TrainingWorkStatus status, string? errorMessage, long now)
    {
        if (IsTerminal(work.Status))
        {
            return;
        }

        work.Status = status;
        work.ErrorMessage = Sanitize(errorMessage);
        work.FinishedAtUtc = now;
        work.Version++;
    }

    private static bool IsTerminal(TrainingWorkStatus status) =>
        status is TrainingWorkStatus.Succeeded or TrainingWorkStatus.Failed or TrainingWorkStatus.Cancelled;

    private static bool IsTerminal(TrainingRunStatus status) =>
        status is TrainingRunStatus.Succeeded or TrainingRunStatus.Failed or TrainingRunStatus.Cancelled;

    private static string? Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        return message.Length > MaxErrorMessageLength ? message[..MaxErrorMessageLength] : message;
    }

    private async Task<TrainingRun> RequireRunAsync(Guid runId, bool tracking, CancellationToken cancellationToken)
    {
        var query = tracking ? _dbContext.TrainingRuns : _dbContext.TrainingRuns.AsNoTracking();
        return await query.FirstOrDefaultAsync(item => item.Id == runId, cancellationToken).ConfigureAwait(false)
               ?? throw new TrainingNotFoundException("The training run was not found.");
    }

    private async Task<TrainingArtifact> RequireArtifactAsync(Guid artifactId, CancellationToken cancellationToken) =>
        await _dbContext.TrainingArtifacts.FirstOrDefaultAsync(item => item.Id == artifactId, cancellationToken).ConfigureAwait(false)
        ?? throw new TrainingNotFoundException("The training artifact was not found.");

    private async Task<TrainingWorkItem?> FindWorkAsync(Guid targetId, bool tracking, CancellationToken cancellationToken)
    {
        var query = tracking ? _dbContext.TrainingWorkItems : _dbContext.TrainingWorkItems.AsNoTracking();
        return await query.FirstOrDefaultAsync(item => item.TargetId == targetId && item.Kind == TrainingWorkKind.TrainingRun, cancellationToken)
                          .ConfigureAwait(false);
    }

    private static void EnsureVersion(long actual, long expected)
    {
        if (actual != expected)
        {
            throw new TrainingConflictException("VersionConflict");
        }
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

    private static TrainingRunRecord ToRecord(TrainingRun entity, TrainingWorkStatus? workStatus, string? workErrorMessage) =>
        new(entity.Id, entity.DatasetId, entity.DatasetContentFingerprint, entity.DatasetRevision, entity.FreezeJson.ToArray(),
            entity.BaseArtifactId, entity.LinkedInstalledModelName, entity.LinkedModelContentFingerprint, entity.OptionsJson.ToArray(),
            OptionalBlob.AsOptionalMemory(entity.LicenseConfirmationJson), entity.Status, OptionalBlob.AsOptionalMemory(entity.ProgressJson),
            entity.LogTail is null ? null : Encoding.UTF8.GetString(entity.LogTail), OptionalBlob.AsOptionalMemory(entity.LaunchReceiptJson),
            entity.ErrorMessage, entity.Version, entity.CreatedAtUtc, entity.UpdatedAtUtc, workStatus, workErrorMessage);

    private static TrainingArtifactRecord ToRecord(TrainingArtifact entity) =>
        new(entity.Id, entity.RunId, entity.Kind, entity.Path, entity.Sha256, entity.SizeBytes, entity.SmokeState,
            entity.SmokeReason, entity.CommittedModelName, entity.Version, entity.CreatedAtUtc, entity.UpdatedAtUtc);
}
