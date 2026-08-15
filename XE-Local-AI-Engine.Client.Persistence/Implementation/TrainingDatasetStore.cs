namespace XE_Local_AI_Engine.Client.Persistence.Implementation;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>
///     <see cref="ITrainingDatasetStore" /> over <see cref="NodeChatDbContext" />. The queue half duplicates
///     <see cref="BenchmarkStore" />'s claim/terminalize shape rather than generalizing it: dataset generation has a
///     single work kind, so the benchmark's <c>(run, kind)</c> pair collapses to one work item per dataset.
/// </summary>
public sealed class TrainingDatasetStore(NodeChatDbContext dbContext, TimeProvider timeProvider) : ITrainingDatasetStore
{
    /// <summary>Fingerprint algorithm tag. Bump it when the canonical serialization below changes.</summary>
    private const string FingerprintPrefix = "v1:";

    private const char FieldSeparator = '\u001f';

    /// <summary>Matches the <c>error_message</c> column's declared max length.</summary>
    private const int MaxErrorMessageLength = 1024;

    private static readonly byte[] RecordSeparator = [0x1e];

    private readonly NodeChatDbContext _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public async Task<TrainingDefinitionRecord> CreateDefinitionAsync(TrainingDefinitionInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        EnsureName(input.Name);

        var now = Now();
        var entity = new TrainingDatasetDefinition
        {
            Id = Guid.NewGuid(),
            Name = input.Name.Trim(),
            Kind = input.Kind,
            DefinitionJson = input.DefinitionJson.ToArray(),
            DefinitionVersion = 1,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _ = _dbContext.TrainingDatasetDefinitions.Add(entity);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(entity);
    }

    public async Task<TrainingDefinitionRecord> UpdateDefinitionAsync(Guid definitionId,
        long expectedVersion,
        TrainingDefinitionInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        EnsureName(input.Name);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var entity = await RequireDefinitionAsync(definitionId, cancellationToken).ConfigureAwait(false);
        EnsureVersion(entity.Version, expectedVersion);

        entity.Name = input.Name.Trim();
        entity.Kind = input.Kind;
        entity.DefinitionJson = input.DefinitionJson.ToArray();
        // An edit produces a new artifact version for datasets to pin, on top of the concurrency bump.
        entity.DefinitionVersion++;
        entity.Version++;
        entity.UpdatedAtUtc = Now();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(entity);
    }

    public async Task<TrainingDefinitionRecord?> GetDefinitionAsync(Guid definitionId, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.TrainingDatasetDefinitions.AsNoTracking()
                                     .FirstOrDefaultAsync(item => item.Id == definitionId, cancellationToken)
                                     .ConfigureAwait(false);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<TrainingDefinitionRecord>> ListDefinitionsAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.TrainingDatasetDefinitions.AsNoTracking()
                                       .OrderBy(item => item.CreatedAtUtc)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);
        return entities.Select(ToRecord).ToArray();
    }

    public async Task DeleteDefinitionAsync(Guid definitionId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var entity = await RequireDefinitionAsync(definitionId, cancellationToken).ConfigureAwait(false);
        EnsureVersion(entity.Version, expectedVersion);
        if (await _dbContext.TrainingDatasets.AnyAsync(item => item.DefinitionId == definitionId, cancellationToken).ConfigureAwait(false))
        {
            throw new TrainingConflictException("DefinitionReferenced");
        }

        _ = _dbContext.TrainingDatasetDefinitions.Remove(entity);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TrainingDatasetRecord> CreateDatasetAndEnqueueAsync(TrainingDatasetEnqueueCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        EnsureName(command.Name);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var definition = await RequireDefinitionAsync(command.DefinitionId, cancellationToken).ConfigureAwait(false);
        EnsureVersion(definition.Version, command.ExpectedDefinitionVersion);

        var now = Now();
        var dataset = new TrainingDataset
        {
            Id = Guid.NewGuid(),
            DefinitionId = definition.Id,
            // The dataset pins the artifact version, not the concurrency token.
            DefinitionVersion = definition.DefinitionVersion,
            Name = command.Name.Trim(),
            Status = TrainingDatasetStatus.Generating,
            Revision = 1,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _ = _dbContext.TrainingDatasets.Add(dataset);
        _ = _dbContext.DatasetGenerationWorkItems.Add(new DatasetGenerationWorkItem
        {
            DatasetId = dataset.Id,
            Status = DatasetGenerationWorkStatus.Queued,
            Attempt = 1,
            Version = 1,
            EnqueuedAtUtc = now
        });

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(dataset, DatasetGenerationWorkStatus.Queued, workErrorMessage: null);
    }

    public async Task<TrainingDatasetRecord?> GetDatasetAsync(Guid datasetId, CancellationToken cancellationToken = default)
    {
        var dataset = await _dbContext.TrainingDatasets.AsNoTracking()
                                      .FirstOrDefaultAsync(item => item.Id == datasetId, cancellationToken)
                                      .ConfigureAwait(false);
        if (dataset is null)
        {
            return null;
        }

        var work = await _dbContext.DatasetGenerationWorkItems.AsNoTracking()
                                   .FirstOrDefaultAsync(item => item.DatasetId == datasetId, cancellationToken)
                                   .ConfigureAwait(false);
        return ToRecord(dataset, work?.Status, work?.ErrorMessage);
    }

    public async Task<IReadOnlyList<TrainingDatasetRecord>> ListDatasetsAsync(CancellationToken cancellationToken = default)
    {
        var datasets = await _dbContext.TrainingDatasets.AsNoTracking()
                                       .OrderByDescending(item => item.CreatedAtUtc)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);
        var work = await _dbContext.DatasetGenerationWorkItems.AsNoTracking()
                                   .ToDictionaryAsync(item => item.DatasetId, cancellationToken)
                                   .ConfigureAwait(false);
        return datasets.Select(dataset => ToRecord(dataset,
                           work.TryGetValue(dataset.Id, out var item) ? item.Status : null,
                           work.TryGetValue(dataset.Id, out var found) ? found.ErrorMessage : null))
                       .ToArray();
    }

    public async Task DeleteDatasetAsync(Guid datasetId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var dataset = await RequireDatasetAsync(datasetId, tracking: true, cancellationToken).ConfigureAwait(false);
        EnsureVersion(dataset.Version, expectedVersion);
        if (await _dbContext.DatasetGenerationWorkItems
                            .AnyAsync(item => item.DatasetId == datasetId
                                              && (item.Status == DatasetGenerationWorkStatus.Queued || item.Status == DatasetGenerationWorkStatus.Running),
                                cancellationToken)
                            .ConfigureAwait(false))
        {
            throw new TrainingConflictException("GenerationActive");
        }

        // A run froze its own copy of this dataset, but its FreezeJson still names the dataset it came from. Deleting
        // the dataset out from under a run would leave that lineage pointing at nothing, so it is refused for as long
        // as any run — including a finished one — references it.
        if (await _dbContext.TrainingRuns.AnyAsync(item => item.DatasetId == datasetId, cancellationToken).ConfigureAwait(false))
        {
            throw new TrainingConflictException("DatasetReferenced");
        }

        // Explicit ordered deletes: the node connection never sets PRAGMA foreign_keys=ON, so the declared cascade on
        // training_dataset_samples never fires. Children first, then the work item, then the dataset itself.
        _ = await _dbContext.TrainingDatasetSamples.Where(item => item.DatasetId == datasetId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        _ = await _dbContext.DatasetGenerationWorkItems.Where(item => item.DatasetId == datasetId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        // ExecuteDelete bypasses the tracker; clearing it stops EF reading the removed children as a severed required
        // association when the parent row is deleted (the benchmark run-delete precedent).
        _dbContext.ChangeTracker.Clear();
        _ = await _dbContext.TrainingDatasets.Where(item => item.Id == datasetId).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> HasActiveGenerationAsync(CancellationToken cancellationToken = default) =>
        _dbContext.DatasetGenerationWorkItems.AsNoTracking()
                  .AnyAsync(item => item.Status == DatasetGenerationWorkStatus.Queued || item.Status == DatasetGenerationWorkStatus.Running,
                      cancellationToken);

    public async Task<DatasetGenerationClaimedWork?> ClaimNextAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var candidate = await _dbContext.DatasetGenerationWorkItems.AsNoTracking()
                                            .Where(item => item.Status == DatasetGenerationWorkStatus.Queued)
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
            var claimed = await _dbContext.DatasetGenerationWorkItems
                                          .Where(item => item.QueueSequence == candidate.QueueSequence
                                                         && item.Version == candidate.Version
                                                         && item.Status == DatasetGenerationWorkStatus.Queued)
                                          .ExecuteUpdateAsync(setters => setters
                                                                         .SetProperty(item => item.Status, DatasetGenerationWorkStatus.Running)
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
            var work = await _dbContext.DatasetGenerationWorkItems.AsNoTracking()
                                       .SingleAsync(item => item.QueueSequence == candidate.QueueSequence, cancellationToken)
                                       .ConfigureAwait(false);
            var dataset = await RequireDatasetAsync(work.DatasetId, tracking: false, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DatasetGenerationClaimedWork(work.QueueSequence,
                work.DatasetId,
                work.Version,
                ToRecord(dataset, work.Status, work.ErrorMessage));
        }
    }

    public async Task<IReadOnlyList<Guid>> RecoverOnStartupAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var interrupted = await _dbContext.DatasetGenerationWorkItems
                                          .Where(item => item.Status == DatasetGenerationWorkStatus.Running)
                                          .ToListAsync(cancellationToken)
                                          .ConfigureAwait(false);
        var now = Now();
        var recovered = new List<Guid>(interrupted.Count);
        foreach (var work in interrupted)
        {
            TerminalizeWork(work, DatasetGenerationWorkStatus.Failed, "Dataset generation was interrupted by a host restart.", now);
            var dataset = await RequireDatasetAsync(work.DatasetId, tracking: true, cancellationToken).ConfigureAwait(false);
            if (dataset.Status == TrainingDatasetStatus.Generating)
            {
                dataset.Status = TrainingDatasetStatus.Failed;
                dataset.Version++;
                dataset.UpdatedAtUtc = now;
            }

            recovered.Add(work.DatasetId);
        }

        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return recovered;
    }

    public async Task<TrainingDatasetRecord> CompleteGenerationAsync(Guid datasetId,
        DatasetGenerationWorkStatus status,
        string? errorMessage,
        CancellationToken cancellationToken = default)
    {
        if (status is DatasetGenerationWorkStatus.Queued or DatasetGenerationWorkStatus.Running)
        {
            throw new TrainingValidationException("Dataset generation can only be completed into a terminal status.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var dataset = await RequireDatasetAsync(datasetId, tracking: true, cancellationToken).ConfigureAwait(false);
        var work = await RequireWorkAsync(datasetId, cancellationToken).ConfigureAwait(false);
        var now = Now();
        if (IsTerminal(work.Status))
        {
            // Idempotent: a startup retrace or a double-terminalize is a silent no-op.
            return ToRecord(dataset, work.Status, work.ErrorMessage);
        }

        TerminalizeWork(work, status, errorMessage, now);
        dataset.Status = status == DatasetGenerationWorkStatus.Succeeded ? TrainingDatasetStatus.Ready : TrainingDatasetStatus.Failed;
        if (status == DatasetGenerationWorkStatus.Succeeded)
        {
            dataset.ContentFingerprint = await ComputeFingerprintAsync(datasetId, cancellationToken).ConfigureAwait(false);
        }

        dataset.Version++;
        dataset.UpdatedAtUtc = now;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(dataset, work.Status, work.ErrorMessage);
    }

    public async Task<TrainingSampleAppendResult> AppendSampleAsync(TrainingSampleInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.SourceHash))
        {
            throw new TrainingValidationException("A training sample requires a source hash.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var dataset = await RequireDatasetAsync(input.DatasetId, tracking: true, cancellationToken).ConfigureAwait(false);
        var now = Now();
        if (await _dbContext.TrainingDatasetSamples
                            .AnyAsync(item => item.DatasetId == input.DatasetId && item.SourceHash == input.SourceHash, cancellationToken)
                            .ConfigureAwait(false))
        {
            dataset.DuplicateSampleCount++;
            dataset.Version++;
            dataset.UpdatedAtUtc = now;
            await SaveAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new TrainingSampleAppendResult(Sample: null, Duplicate: true);
        }

        var nextSequence = await _dbContext.TrainingDatasetSamples
                                           .Where(item => item.DatasetId == input.DatasetId)
                                           .Select(item => (int?)item.Sequence)
                                           .MaxAsync(cancellationToken)
                                           .ConfigureAwait(false) ?? -1;
        var sample = new TrainingDatasetSample
        {
            Id = Guid.NewGuid(),
            DatasetId = input.DatasetId,
            Sequence = nextSequence + 1,
            Kind = input.Kind,
            Label = input.Label,
            ReviewState = TrainingSampleReviewState.Pending,
            ContentJson = input.ContentJson.ToArray(),
            ValidationJson = input.ValidationJson?.ToArray(),
            Provenance = input.Provenance,
            SourceHash = input.SourceHash,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _ = _dbContext.TrainingDatasetSamples.Add(sample);

        dataset.TotalSampleCount++;
        if (input.Label == TrainingSampleLabel.Good)
        {
            dataset.GoodSampleCount++;
        }
        else
        {
            dataset.BadSampleCount++;
        }

        // The fingerprint is computed once, when generation terminalizes: recomputing it per appended sample would be
        // quadratic over the whole dataset, and the value is meaningless while the dataset is still Generating.
        dataset.Revision++;
        dataset.Version++;
        dataset.UpdatedAtUtc = now;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new TrainingSampleAppendResult(ToRecord(sample), Duplicate: false);
    }

    public async Task RecordRejectedSampleAsync(Guid datasetId, CancellationToken cancellationToken = default)
    {
        var dataset = await RequireDatasetAsync(datasetId, tracking: true, cancellationToken).ConfigureAwait(false);
        dataset.RejectedSampleCount++;
        dataset.Version++;
        dataset.UpdatedAtUtc = Now();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TrainingSamplePage> ListSamplesAsync(TrainingSampleQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Page < 1 || query.PageSize is < 1 or > 200)
        {
            throw new TrainingValidationException("Page must be positive and pageSize must be between 1 and 200.");
        }

        var filtered = _dbContext.TrainingDatasetSamples.AsNoTracking().Where(item => item.DatasetId == query.DatasetId);
        if (query.Label is { } label)
        {
            filtered = filtered.Where(item => item.Label == label);
        }

        if (query.ReviewState is { } reviewState)
        {
            filtered = filtered.Where(item => item.ReviewState == reviewState);
        }

        if (!string.IsNullOrWhiteSpace(query.Kind))
        {
            filtered = filtered.Where(item => item.Kind == query.Kind);
        }

        var total = await filtered.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await filtered.OrderBy(item => item.Sequence)
                                  .Skip((query.Page - 1) * query.PageSize)
                                  .Take(query.PageSize)
                                  .ToListAsync(cancellationToken)
                                  .ConfigureAwait(false);
        return new TrainingSamplePage(items.Select(ToRecord).ToArray(), total);
    }

    public async Task<IReadOnlyList<TrainingSampleRecord>> ListAllSamplesAsync(Guid datasetId, CancellationToken cancellationToken = default)
    {
        var items = await _dbContext.TrainingDatasetSamples.AsNoTracking()
                                    .Where(item => item.DatasetId == datasetId)
                                    .OrderBy(item => item.Sequence)
                                    .ToListAsync(cancellationToken)
                                    .ConfigureAwait(false);
        return items.Select(ToRecord).ToArray();
    }

    public async Task<TrainingSampleRecord> ReviewSampleAsync(TrainingSampleReviewCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Verb == TrainingSampleReviewVerb.Relabel && command.Label is null)
        {
            throw new TrainingValidationException("A relabel review requires the target label.");
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var sample = await _dbContext.TrainingDatasetSamples
                                     .FirstOrDefaultAsync(item => item.Id == command.SampleId, cancellationToken)
                                     .ConfigureAwait(false)
                     ?? throw new TrainingNotFoundException("The training sample was not found.");
        var dataset = await RequireDatasetAsync(sample.DatasetId, tracking: true, cancellationToken).ConfigureAwait(false);
        if (dataset.Status == TrainingDatasetStatus.Generating)
        {
            throw new TrainingConflictException("GenerationActive");
        }

        var now = Now();
        switch (command.Verb)
        {
            case TrainingSampleReviewVerb.Approve:
                sample.ReviewState = TrainingSampleReviewState.Approved;
                break;
            case TrainingSampleReviewVerb.Reject:
                sample.ReviewState = TrainingSampleReviewState.Rejected;
                break;
            case TrainingSampleReviewVerb.Relabel when command.Label is { } target:
                ApplyLabelCounts(dataset, sample.Label, target);
                sample.Label = target;
                break;
            default:
                throw new TrainingValidationException("Unknown training sample review verb.");
        }

        sample.UpdatedAtUtc = now;
        // Any sample mutation bumps the revision and recomputes the fingerprint — the freeze key must never silently
        // drift out of step with the sample set a run would read.
        dataset.Revision++;
        dataset.Version++;
        dataset.UpdatedAtUtc = now;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        dataset.ContentFingerprint = await ComputeFingerprintAsync(dataset.Id, cancellationToken).ConfigureAwait(false);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(sample);
    }

    public async Task<ToolMockRecord> CreateMockAsync(ToolMockInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        EnsureName(input.ToolName);

        var now = Now();
        var entity = new ToolMockDefinition
        {
            Id = Guid.NewGuid(),
            ToolName = input.ToolName.Trim(),
            MockJson = input.MockJson.ToArray(),
            VerificationState = ToolMockVerificationState.Unverified,
            // A freshly authored or edited mock is never usable until the static verifier passes it.
            Enabled = input.Enabled,
            Version = 1,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        _ = _dbContext.ToolMockDefinitions.Add(entity);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(entity);
    }

    public async Task<ToolMockRecord> UpdateMockAsync(Guid mockId, long expectedVersion, ToolMockInput input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        EnsureName(input.ToolName);

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var entity = await RequireMockAsync(mockId, cancellationToken).ConfigureAwait(false);
        EnsureVersion(entity.Version, expectedVersion);
        entity.ToolName = input.ToolName.Trim();
        entity.MockJson = input.MockJson.ToArray();
        entity.Enabled = input.Enabled;
        // An edited body invalidates the previous verdict: verification is over the body, so it must be re-run.
        entity.VerificationState = ToolMockVerificationState.Unverified;
        entity.VerificationJson = null;
        entity.Version++;
        entity.UpdatedAtUtc = Now();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(entity);
    }

    public async Task<ToolMockRecord?> GetMockAsync(Guid mockId, CancellationToken cancellationToken = default)
    {
        var entity = await _dbContext.ToolMockDefinitions.AsNoTracking()
                                     .FirstOrDefaultAsync(item => item.Id == mockId, cancellationToken)
                                     .ConfigureAwait(false);
        return entity is null ? null : ToRecord(entity);
    }

    public async Task<IReadOnlyList<ToolMockRecord>> ListMocksAsync(CancellationToken cancellationToken = default)
    {
        var entities = await _dbContext.ToolMockDefinitions.AsNoTracking()
                                       .OrderBy(item => item.ToolName)
                                       .ThenBy(item => item.CreatedAtUtc)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);
        return entities.Select(ToRecord).ToArray();
    }

    public async Task<IReadOnlyList<ToolMockRecord>> ListUsableMocksAsync(string toolName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        var entities = await _dbContext.ToolMockDefinitions.AsNoTracking()
                                       .Where(item => item.ToolName == toolName
                                                      && item.Enabled
                                                      && item.VerificationState == ToolMockVerificationState.Verified)
                                       .OrderBy(item => item.CreatedAtUtc)
                                       .ToListAsync(cancellationToken)
                                       .ConfigureAwait(false);
        return entities.Select(ToRecord).ToArray();
    }

    public async Task DeleteMockAsync(Guid mockId, long expectedVersion, CancellationToken cancellationToken = default)
    {
        var entity = await RequireMockAsync(mockId, cancellationToken).ConfigureAwait(false);
        EnsureVersion(entity.Version, expectedVersion);
        _ = _dbContext.ToolMockDefinitions.Remove(entity);
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ToolMockRecord> SetMockVerificationAsync(Guid mockId,
        long expectedVersion,
        ToolMockVerificationState state,
        ReadOnlyMemory<byte> verificationJson,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var entity = await RequireMockAsync(mockId, cancellationToken).ConfigureAwait(false);
        EnsureVersion(entity.Version, expectedVersion);
        entity.VerificationState = state;
        entity.VerificationJson = verificationJson.ToArray();
        if (state != ToolMockVerificationState.Verified)
        {
            // A rejected mock cannot stay active — there is no fallthrough to real execution, so leaving it enabled
            // would only ever produce a silent validation-only outcome.
            entity.Enabled = false;
        }

        entity.Version++;
        entity.UpdatedAtUtc = Now();
        await SaveAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToRecord(entity);
    }

    /// <summary>
    ///     <c>v1:</c> + SHA-256 hex over the samples in canonical order (ascending <see cref="TrainingDatasetSample.Sequence" />),
    ///     each contributing sequence, kind, label, review state and the raw decrypted content bytes. Review state
    ///     participates because it decides membership: an approve/reject changes what a run would freeze.
    /// </summary>
    private async Task<string> ComputeFingerprintAsync(Guid datasetId, CancellationToken cancellationToken)
    {
        var samples = await _dbContext.TrainingDatasetSamples.AsNoTracking()
                                      .Where(item => item.DatasetId == datasetId)
                                      .OrderBy(item => item.Sequence)
                                      .ToListAsync(cancellationToken)
                                      .ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var sample in samples)
        {
            var header = string.Create(CultureInfo.InvariantCulture,
                $"{sample.Sequence}{FieldSeparator}{sample.Kind}{FieldSeparator}{sample.Label}{FieldSeparator}{sample.ReviewState}{FieldSeparator}");
            hash.AppendData(Encoding.UTF8.GetBytes(header));
            hash.AppendData(sample.ContentJson);
            hash.AppendData(RecordSeparator);
        }

        return FingerprintPrefix + Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void ApplyLabelCounts(TrainingDataset dataset, TrainingSampleLabel from, TrainingSampleLabel to)
    {
        if (from == to)
        {
            return;
        }

        if (from == TrainingSampleLabel.Good)
        {
            dataset.GoodSampleCount--;
            dataset.BadSampleCount++;
        }
        else
        {
            dataset.BadSampleCount--;
            dataset.GoodSampleCount++;
        }
    }

    private static void TerminalizeWork(DatasetGenerationWorkItem work, DatasetGenerationWorkStatus status, string? errorMessage, long now)
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

    private static bool IsTerminal(DatasetGenerationWorkStatus status) =>
        status is DatasetGenerationWorkStatus.Succeeded or DatasetGenerationWorkStatus.Failed or DatasetGenerationWorkStatus.Cancelled;

    private static string? Sanitize(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        return message.Length > MaxErrorMessageLength ? message[..MaxErrorMessageLength] : message;
    }

    private async Task<TrainingDatasetDefinition> RequireDefinitionAsync(Guid definitionId, CancellationToken cancellationToken) =>
        await _dbContext.TrainingDatasetDefinitions.FirstOrDefaultAsync(item => item.Id == definitionId, cancellationToken).ConfigureAwait(false)
        ?? throw new TrainingNotFoundException("The training dataset definition was not found.");

    private async Task<TrainingDataset> RequireDatasetAsync(Guid datasetId, bool tracking, CancellationToken cancellationToken)
    {
        var query = tracking ? _dbContext.TrainingDatasets : _dbContext.TrainingDatasets.AsNoTracking();
        return await query.FirstOrDefaultAsync(item => item.Id == datasetId, cancellationToken).ConfigureAwait(false)
               ?? throw new TrainingNotFoundException("The training dataset was not found.");
    }

    private async Task<DatasetGenerationWorkItem> RequireWorkAsync(Guid datasetId, CancellationToken cancellationToken) =>
        await _dbContext.DatasetGenerationWorkItems.FirstOrDefaultAsync(item => item.DatasetId == datasetId, cancellationToken).ConfigureAwait(false)
        ?? throw new TrainingNotFoundException("The dataset generation work item was not found.");

    private async Task<ToolMockDefinition> RequireMockAsync(Guid mockId, CancellationToken cancellationToken) =>
        await _dbContext.ToolMockDefinitions.FirstOrDefaultAsync(item => item.Id == mockId, cancellationToken).ConfigureAwait(false)
        ?? throw new TrainingNotFoundException("The tool mock was not found.");

    private static void EnsureVersion(long actual, long expected)
    {
        if (actual != expected)
        {
            throw new TrainingConflictException("VersionConflict");
        }
    }

    private static void EnsureName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new TrainingValidationException("A name is required.");
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

    private static TrainingDefinitionRecord ToRecord(TrainingDatasetDefinition entity) =>
        new(entity.Id, entity.Name, entity.Kind, entity.DefinitionJson.ToArray(), entity.DefinitionVersion, entity.Version,
            entity.CreatedAtUtc, entity.UpdatedAtUtc);

    private static TrainingDatasetRecord ToRecord(TrainingDataset entity, DatasetGenerationWorkStatus? workStatus, string? workErrorMessage) =>
        new(entity.Id, entity.DefinitionId, entity.DefinitionVersion, entity.Name, entity.Status, entity.Revision, entity.ContentFingerprint,
            entity.TotalSampleCount, entity.GoodSampleCount, entity.BadSampleCount, entity.RejectedSampleCount, entity.DuplicateSampleCount,
            entity.Version, entity.CreatedAtUtc, entity.UpdatedAtUtc, workStatus, workErrorMessage);

    private static TrainingSampleRecord ToRecord(TrainingDatasetSample entity) =>
        new(entity.Id, entity.DatasetId, entity.Sequence, entity.Kind, entity.Label, entity.ReviewState, entity.ContentJson.ToArray(),
            // `?.ToArray()` would read back as an EMPTY memory rather than as null — see OptionalBlob.
            OptionalBlob.AsOptionalMemory(entity.ValidationJson), entity.Provenance, entity.SourceHash, entity.CreatedAtUtc, entity.UpdatedAtUtc);

    private static ToolMockRecord ToRecord(ToolMockDefinition entity) =>
        new(entity.Id, entity.ToolName, entity.MockJson.ToArray(), OptionalBlob.AsOptionalMemory(entity.VerificationJson), entity.VerificationState,
            entity.Enabled, entity.Version, entity.CreatedAtUtc, entity.UpdatedAtUtc);
}
