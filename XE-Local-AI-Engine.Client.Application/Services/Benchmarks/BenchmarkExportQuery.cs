namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Stores;

public interface IBenchmarkExportQuery
{
    Task<BenchmarkJsonExportQueryResult?> GetJsonAsync(Guid projectId, CancellationToken ct);

    Task<BenchmarkCsvExportQueryResult?> GetCsvAsync(Guid projectId, CancellationToken ct);
}

public sealed record BenchmarkJsonExportQueryResult(
    BenchmarkProjectRecord Project,
    IReadOnlyList<BenchmarkRunRecord> Summaries,
    IReadOnlyList<BenchmarkExportRunQueryItem> Runs,
    BenchmarkRankCohort? RankCohort,
    BenchmarkJudgePolicyRevisionRecord? JudgePolicyRevision,
    BenchmarkPairwiseFitRecord? PairwiseFit,
    BenchmarkFidelityDisplayFacts Fidelity,
    IReadOnlyDictionary<Guid, BenchmarkExportRunFacts> Facts,
    IReadOnlyList<BenchmarkTaskItemRecord> TaskItems,
    BenchmarkCellPage Cells);

public sealed record BenchmarkCsvExportQueryResult(
    BenchmarkProjectRecord Project,
    IReadOnlyList<BenchmarkRunRecord> Runs,
    BenchmarkPairwiseFitRecord? PairwiseFit,
    BenchmarkFidelityDisplayFacts Fidelity);

public sealed record BenchmarkExportRunQueryItem(
    BenchmarkRunRecord Summary,
    BenchmarkRunRecord Full,
    BenchmarkJudgeResultV2? Verdict);

public sealed record BenchmarkExportRunFacts(
    string? BuildCommit,
    string? GpuInfo,
    string? ModelFilename,
    long? ModelSizeBytes,
    int? GpuLayers)
{
    public static BenchmarkExportRunFacts Empty { get; } = new(null, null, null, null, null);
}

internal sealed class BenchmarkExportQuery(
    IBenchmarkStore store,
    IBenchmarkExportFactsResolver factsResolver) : IBenchmarkExportQuery
{
    private readonly IBenchmarkExportFactsResolver _factsResolver = factsResolver ?? throw new ArgumentNullException(nameof(factsResolver));
    private readonly IBenchmarkStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<BenchmarkJsonExportQueryResult?> GetJsonAsync(Guid projectId, CancellationToken ct)
    {
        var project = await _store.GetProjectAsync(projectId, ct).ConfigureAwait(false);
        if (project is null)
        {
            return null;
        }

        var page = await _store.ListAllRunsAsync(projectId, ct).ConfigureAwait(false);
        var firstOfMeasuredGroups = FirstOfMeasuredGroups(page.Items);
        var runs = new List<BenchmarkExportRunQueryItem>(page.Items.Count);
        var facts = new Dictionary<Guid, BenchmarkExportRunFacts>(firstOfMeasuredGroups.Count);
        foreach (var summary in page.Items)
        {
            var full = await _store.GetRunAsync(summary.Id, ct).ConfigureAwait(false);
            if (full is null)
            {
                continue;
            }

            runs.Add(new BenchmarkExportRunQueryItem(summary, full, await ReadVerdictAsync(full, ct).ConfigureAwait(false)));
            if (firstOfMeasuredGroups.Contains(full.Id))
            {
                facts[full.Id] = _factsResolver.ResolveRun(full);
            }
        }

        return new BenchmarkJsonExportQueryResult(project,
            page.Items,
            runs,
            page.RankCohort,
            await _store.GetCurrentJudgePolicyRevisionAsync(projectId, ct).ConfigureAwait(false),
            await _store.GetActivePairwiseFitAsync(projectId, ct).ConfigureAwait(false),
            _factsResolver.ResolveProject(project),
            facts,
            await _store.ListTaskItemsAsync(projectId, ct).ConfigureAwait(false),
            await _store.ListCellsAsync(projectId, ct).ConfigureAwait(false));
    }

    public async Task<BenchmarkCsvExportQueryResult?> GetCsvAsync(Guid projectId, CancellationToken ct)
    {
        var project = await _store.GetProjectAsync(projectId, ct).ConfigureAwait(false);
        if (project is null)
        {
            return null;
        }

        var page = await _store.ListAllRunsAsync(projectId, ct).ConfigureAwait(false);
        return new BenchmarkCsvExportQueryResult(project,
            page.Items,
            await _store.GetActivePairwiseFitAsync(projectId, ct).ConfigureAwait(false),
            _factsResolver.ResolveProject(project));
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

        var attempt = await _store.GetJudgeAttemptAsync(attemptId, ct).ConfigureAwait(false);
        return BenchmarkJudgeSerialization.DeserializeResult(attempt?.ResultJson);
    }
}
