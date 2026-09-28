namespace XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Stores;

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
