namespace XE_Local_AI_Engine.Tests.ModelFit.Fakes;

using XE_Local_AI_Engine.Client.Persistence;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.ModelFit.Catalog;
using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>
///     In-memory <see cref="IModelFitSnapshotStore" /> for refresh tests. Mirrors the production store's
///     contract: assigns ids/timestamps on create, stamps terminal fields, and enforces the single-latest-successful
///     invariant per <c>(operation, use_case, provider_name, model_name)</c> key on a Succeeded terminal transition.
///     It exposes the stored rows so tests can assert status / raw / diagnostics / latest-successful without decryption.
/// </summary>
internal sealed class InMemoryModelFitSnapshotStore : IModelFitSnapshotStore
{
    public Dictionary<Guid, StoredSnapshot> Snapshots { get; } = [];

    public Task<ModelFitSnapshotSummaryRecord> CreateRunningAsync(ModelFitSnapshotInput input, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var stored = new StoredSnapshot
        {
            Id = id,
            ApprovedImageId = input.ApprovedImageId,
            Operation = input.Operation,
            UseCase = input.UseCase,
            ProviderName = input.ProviderName,
            ModelName = input.ModelName,
            Status = input.Status,
            StartedAtUtc = input.StartedAtUtc,
            CreatedByRunId = input.CreatedByRunId,
            CreatedAtUtc = 1L
        };
        Snapshots[id] = stored;
        return Task.FromResult(stored.ToSummary());
    }

    public Task<ModelFitSnapshotSummaryRecord?> MarkTerminalAsync(Guid id,
        ModelFitRunStatus status,
        int? exitCode,
        long? durationMs,
        string? rawJson,
        string? stderrExcerpt,
        string? diagnosticsJson,
        long completedAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (!Snapshots.TryGetValue(id, out var snapshot))
        {
            return Task.FromResult<ModelFitSnapshotSummaryRecord?>(null);
        }

        snapshot.Status = status;
        snapshot.ExitCode = exitCode;
        snapshot.DurationMs = durationMs;
        snapshot.RawJson = rawJson;
        snapshot.StderrExcerpt = stderrExcerpt;
        snapshot.DiagnosticsJson = diagnosticsJson;
        snapshot.CompletedAtUtc = completedAtUtc;

        if (status == ModelFitRunStatus.Succeeded)
        {
            // Transactional latest-replace: clear prior latest for the same key, then set this row.
            foreach (var other in Snapshots.Values)
            {
                if (other.Id != id && other.IsLatestSuccessful && SameKey(other, snapshot))
                {
                    other.IsLatestSuccessful = false;
                }
            }

            snapshot.IsLatestSuccessful = true;
        }

        return Task.FromResult<ModelFitSnapshotSummaryRecord?>(snapshot.ToSummary());
    }

    public Task<ModelFitSnapshotSummaryRecord?> GetLatestSuccessfulSummaryAsync(ModelFitOperation operation,
        string? useCase,
        string providerName,
        string? modelName,
        CancellationToken cancellationToken = default)
    {
        var match = Snapshots.Values.FirstOrDefault(s =>
            s.IsLatestSuccessful
            && s.Operation == operation
            && s.UseCase == useCase
            && s.ProviderName == providerName
            && s.ModelName == modelName);
        return Task.FromResult(match?.ToSummary());
    }

    public Task<IReadOnlyList<ModelFitSnapshotSummaryRecord>> ListRecentSummariesAsync(ModelFitOperation? operation = null,
        string? providerName = null,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var summaries = Snapshots.Values
                                 .Where(s => (operation is null || s.Operation == operation) && (providerName is null || s.ProviderName == providerName))
                                 .OrderByDescending(s => s.CreatedAtUtc)
                                 .Take(limit)
                                 .Select(s => s.ToSummary())
                                 .ToArray();
        return Task.FromResult<IReadOnlyList<ModelFitSnapshotSummaryRecord>>(summaries);
    }

    public Task<ModelFitSnapshotRawRecord?> GetRawByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!Snapshots.TryGetValue(id, out var snapshot))
        {
            return Task.FromResult<ModelFitSnapshotRawRecord?>(null);
        }

        return Task.FromResult<ModelFitSnapshotRawRecord?>(new ModelFitSnapshotRawRecord(id, snapshot.RawJson, snapshot.StderrExcerpt, snapshot.DiagnosticsJson));
    }

    private static bool SameKey(StoredSnapshot a, StoredSnapshot b)
    {
        return a.Operation == b.Operation && a.UseCase == b.UseCase && a.ProviderName == b.ProviderName && a.ModelName == b.ModelName;
    }

    internal sealed class StoredSnapshot
    {
        public required Guid Id { get; init; }
        public required string ApprovedImageId { get; init; }
        public required ModelFitOperation Operation { get; init; }
        public string? UseCase { get; init; }
        public required string ProviderName { get; init; }
        public string? ModelName { get; init; }
        public ModelFitRunStatus Status { get; set; }
        public long? StartedAtUtc { get; init; }
        public long? CompletedAtUtc { get; set; }
        public long? DurationMs { get; set; }
        public int? ExitCode { get; set; }
        public bool IsLatestSuccessful { get; set; }
        public Guid? CreatedByRunId { get; init; }
        public long CreatedAtUtc { get; init; }
        public string? RawJson { get; set; }
        public string? StderrExcerpt { get; set; }
        public string? DiagnosticsJson { get; set; }

        public ModelFitSnapshotSummaryRecord ToSummary()
        {
            return new ModelFitSnapshotSummaryRecord(Id, ApprovedImageId, Operation, UseCase, ProviderName, ModelName, Status, StartedAtUtc, CompletedAtUtc,
                DurationMs, ExitCode, IsLatestSuccessful, CreatedByRunId, CreatedAtUtc);
        }
    }
}

/// <summary>In-memory <see cref="IModelFitRecommendationStore" /> for refresh tests: replace-by-snapshot + ordered read.</summary>
internal sealed class InMemoryModelFitRecommendationStore : IModelFitRecommendationStore
{
    private readonly Dictionary<Guid, List<ModelFitRecommendationRecord>> _rows = [];

    public Task<int> ReplaceForSnapshotAsync(Guid snapshotId, IReadOnlyList<ModelFitRecommendationInput> recommendations, CancellationToken cancellationToken = default)
    {
        var rows = recommendations
                   .Select(input => new ModelFitRecommendationRecord(Guid.NewGuid(), snapshotId, input.Rank, input.ModelName, input.ProviderModelName, input.Score,
                       input.FitLevel, input.RunMode, input.Quantization, input.EstimatedTokensPerSecond, input.RequiredRamMb,
                       input.RequiredVramMb, input.ContextTokens, input.IsInstalled, input.PullModelName, input.DiagnosticsJson))
                   .ToList();
        _rows[snapshotId] = rows;
        return Task.FromResult(rows.Count);
    }

    public Task<IReadOnlyList<ModelFitRecommendationRecord>> ListForSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(RowsFor(snapshotId));
    }

    public IReadOnlyList<ModelFitRecommendationRecord> RowsFor(Guid snapshotId)
    {
        return _rows.TryGetValue(snapshotId, out var rows) ? rows.OrderBy(r => r.Rank).ToArray() : [];
    }
}

/// <summary>
///     No-op <see cref="ICatalogRecommendationService" /> for tests that exercise only the explore (live-HF) lane: the
///     empty bundled catalog is a valid, always-available <see cref="ModelCatalogDocument" />, so the catalog lane
///     contributes zero rows and every pre-existing explore-lane assertion is unaffected.
/// </summary>
internal sealed class EmptyCatalogRecommendationService : ICatalogRecommendationService
{
    public Task<CatalogRecommendationResult> BuildRecommendationsAsync(string? useCase,
        string quantCeiling,
        int ctxTarget,
        HardwareProfile profile,
        IReadOnlySet<string> installedKeys,
        CancellationToken cancellationToken)
    {
        var emptyDocument = new ModelCatalogDocument(SchemaVersion: 1, "test-empty", UpdatedAt: null, Models: []);
        var snapshot = new ModelCatalogSnapshot(emptyDocument, ModelCatalogSource.Bundled, FetchedAtUtc: null, SourceUrl: null);
        return Task.FromResult(new CatalogRecommendationResult([], [], snapshot));
    }
}
