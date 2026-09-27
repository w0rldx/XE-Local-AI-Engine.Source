namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Stores;

public interface IBenchmarkExportQuery
{
    Task<BenchmarkJsonExportQueryResult?> GetJsonAsync(Guid projectId, CancellationToken ct);

    Task<BenchmarkCsvExportQueryResult?> GetCsvAsync(Guid projectId, CancellationToken ct);
}

public sealed class BenchmarkJsonExportQueryResult
{
    public required BenchmarkProjectRecord Project { get; init; }

    public required IReadOnlyList<BenchmarkRunRecord> Summaries { get; init; }

    public required IReadOnlyList<BenchmarkExportRunQueryItem> Runs { get; init; }

    public required BenchmarkRankCohort? RankCohort { get; init; }

    public required BenchmarkJudgePolicyRevisionRecord? JudgePolicyRevision { get; init; }

    public required BenchmarkPairwiseFitRecord? PairwiseFit { get; init; }

    /// <summary>The fit's per-run scores; empty without a fit or when its stored scores are unreadable.</summary>
    public required IReadOnlyList<BenchmarkPairwiseScoreEntry> PairwiseScores { get; init; }

    public required BenchmarkFidelityDisplayFacts Fidelity { get; init; }

    public required IReadOnlyDictionary<Guid, BenchmarkExportRunFacts> Facts { get; init; }

    public required IReadOnlyList<BenchmarkTaskItemRecord> TaskItems { get; init; }

    public required BenchmarkCellPage Cells { get; init; }
}

public sealed class BenchmarkCsvExportQueryResult
{
    public required BenchmarkProjectRecord Project { get; init; }

    public required IReadOnlyList<BenchmarkRunRecord> Runs { get; init; }

    public required BenchmarkPairwiseFitRecord? PairwiseFit { get; init; }

    /// <summary>The fit's per-run scores; empty without a fit or when its stored scores are unreadable.</summary>
    public required IReadOnlyList<BenchmarkPairwiseScoreEntry> PairwiseScores { get; init; }

    public required BenchmarkFidelityDisplayFacts Fidelity { get; init; }
}

public sealed class BenchmarkExportRunQueryItem
{
    public required BenchmarkRunRecord Summary { get; init; }

    public required BenchmarkRunRecord Full { get; init; }

    public required BenchmarkJudgeResultV2? Verdict { get; init; }
}

public sealed class BenchmarkExportRunFacts
{
    public required string? BuildCommit { get; init; }

    public required string? GpuInfo { get; init; }

    public required string? ModelFilename { get; init; }

    public required long? ModelSizeBytes { get; init; }

    public required int? GpuLayers { get; init; }

    public static BenchmarkExportRunFacts Empty { get; } = new()
    {
        BuildCommit = null,
        GpuInfo = null,
        ModelFilename = null,
        ModelSizeBytes = null,
        GpuLayers = null
    };
}

internal sealed class BenchmarkExportQuery : IBenchmarkExportQuery
{
    private readonly IBenchmarkExportFactsResolver _factsResolver;
    private readonly ILogger<BenchmarkExportQuery> _logger;
    private readonly IBenchmarkStore _store;

    public BenchmarkExportQuery(IBenchmarkStore store,
        IBenchmarkExportFactsResolver factsResolver,
        ILogger<BenchmarkExportQuery> logger)
    {
        ArgumentNullException.ThrowIfNull(factsResolver);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _factsResolver = factsResolver;
        _store = store;
        _logger = logger;
    }

    public async Task<BenchmarkJsonExportQueryResult?> GetJsonAsync(Guid projectId, CancellationToken ct)
    {
        var project = await _store.GetProjectAsync(projectId, ct);
        if (project is null)
        {
            return null;
        }

        var page = await _store.ListAllRunsAsync(projectId, ct);
        var firstOfMeasuredGroups = FirstOfMeasuredGroups(page.Items);
        var runs = new List<BenchmarkExportRunQueryItem>(page.Items.Count);
        var facts = new Dictionary<Guid, BenchmarkExportRunFacts>(firstOfMeasuredGroups.Count);
        foreach (var summary in page.Items)
        {
            var full = await _store.GetRunAsync(summary.Id, ct);
            if (full is null)
            {
                continue;
            }

            runs.Add(new BenchmarkExportRunQueryItem
            {
                Summary = summary,
                Full = full,
                Verdict = await ReadVerdictAsync(full, ct)
            });
            if (firstOfMeasuredGroups.Contains(full.Id))
            {
                facts[full.Id] = _factsResolver.ResolveRun(full);
            }
        }

        var pairwiseFit = await _store.GetActivePairwiseFitAsync(projectId, ct);
        return new BenchmarkJsonExportQueryResult
        {
            Project = project,
            Summaries = page.Items,
            Runs = runs,
            RankCohort = page.RankCohort,
            JudgePolicyRevision = await _store.GetCurrentJudgePolicyRevisionAsync(projectId, ct),
            PairwiseFit = pairwiseFit,
            PairwiseScores = BenchmarkPairwiseFitScores.Read(pairwiseFit, _logger),
            Fidelity = _factsResolver.ResolveProject(project),
            Facts = facts,
            TaskItems = await _store.ListTaskItemsAsync(projectId, ct),
            Cells = await _store.ListCellsAsync(projectId, ct)
        };
    }

    public async Task<BenchmarkCsvExportQueryResult?> GetCsvAsync(Guid projectId, CancellationToken ct)
    {
        var project = await _store.GetProjectAsync(projectId, ct);
        if (project is null)
        {
            return null;
        }

        var page = await _store.ListAllRunsAsync(projectId, ct);
        var pairwiseFit = await _store.GetActivePairwiseFitAsync(projectId, ct);
        return new BenchmarkCsvExportQueryResult
        {
            Project = project,
            Runs = page.Items,
            PairwiseFit = pairwiseFit,
            PairwiseScores = BenchmarkPairwiseFitScores.Read(pairwiseFit, _logger),
            Fidelity = _factsResolver.ResolveProject(project)
        };
    }

    private static HashSet<Guid> FirstOfMeasuredGroups(IReadOnlyList<BenchmarkRunRecord> runs) =>
        runs.Where(static run => !run.IsWarmup && run.Throughput is not null)
            .GroupBy(static run => run.RepeatGroupId ?? run.Id)
            .Select(static group => group.OrderBy(static run => run.RepeatIndex ?? 0)
                                         .ThenBy(static run => run.CreatedAtUtc)
                                         .First()
                                         .Id)
            .ToHashSet();

    private async Task<BenchmarkJudgeResultV2?> ReadVerdictAsync(BenchmarkRunRecord run, CancellationToken ct)
    {
        if (run.Judge?.AttemptId is not { } attemptId)
        {
            return null;
        }

        var attempt = await _store.GetJudgeAttemptAsync(attemptId, ct);
        return BenchmarkJudgeSerialization.DeserializeResult(attempt?.ResultJson);
    }
}
