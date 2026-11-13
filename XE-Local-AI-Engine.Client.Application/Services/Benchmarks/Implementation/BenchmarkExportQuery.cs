namespace XE_Local_AI_Engine.Client.Services.Benchmarks.Implementation;

using XE_Local_AI_Engine.Client.Persistence.Stores;

internal sealed class BenchmarkExportQuery : IBenchmarkExportQuery
{
    private readonly IBenchmarkExportFactsResolver _factsResolver;
    private readonly ILogger<BenchmarkExportQuery> _logger;
    private readonly IBenchmarkProjectStore _projectStore;
    private readonly IBenchmarkTaskItemStore _taskItemStore;
    private readonly IBenchmarkRunStore _runStore;
    private readonly IBenchmarkJudgeStore _judgeStore;
    private readonly IBenchmarkPairwiseStore _pairwiseStore;

    public BenchmarkExportQuery(IBenchmarkProjectStore projectStore,
        IBenchmarkTaskItemStore taskItemStore,
        IBenchmarkRunStore runStore,
        IBenchmarkJudgeStore judgeStore,
        IBenchmarkPairwiseStore pairwiseStore,
        IBenchmarkExportFactsResolver factsResolver,
        ILogger<BenchmarkExportQuery> logger)
    {
        ArgumentNullException.ThrowIfNull(factsResolver);
        ArgumentNullException.ThrowIfNull(projectStore);
        ArgumentNullException.ThrowIfNull(taskItemStore);
        ArgumentNullException.ThrowIfNull(runStore);
        ArgumentNullException.ThrowIfNull(judgeStore);
        ArgumentNullException.ThrowIfNull(pairwiseStore);
        ArgumentNullException.ThrowIfNull(logger);
        _factsResolver = factsResolver;
        _projectStore = projectStore;
        _taskItemStore = taskItemStore;
        _runStore = runStore;
        _judgeStore = judgeStore;
        _pairwiseStore = pairwiseStore;
        _logger = logger;
    }

    public async Task<BenchmarkJsonExportQueryResult> GetJsonAsync(Guid projectId, CancellationToken ct)
    {
        var project = await _projectStore.GetProjectAsync(projectId, ct) ?? throw new BenchmarkNotFoundException("Benchmark project was not found.");

        var page = await _runStore.ListAllRunsAsync(projectId, ct);
        var firstOfMeasuredGroups = FirstOfMeasuredGroups(page.Items);
        var runs = new List<BenchmarkExportRunQueryItem>(page.Items.Count);
        var facts = new Dictionary<Guid, BenchmarkExportRunFacts>(firstOfMeasuredGroups.Count);
        foreach (var summary in page.Items)
        {
            var full = await _runStore.GetRunAsync(summary.Id, ct);
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

        var pairwiseFit = await _pairwiseStore.GetActivePairwiseFitAsync(projectId, ct);
        return new BenchmarkJsonExportQueryResult
        {
            Project = project,
            Summaries = page.Items,
            Runs = runs,
            RankCohort = page.RankCohort,
            JudgePolicyRevision = await _judgeStore.GetCurrentJudgePolicyRevisionAsync(projectId, ct),
            PairwiseFit = pairwiseFit,
            PairwiseScores = BenchmarkPairwiseFitScores.Read(pairwiseFit, _logger),
            Fidelity = _factsResolver.ResolveProject(project),
            Facts = facts,
            TaskItems = await _taskItemStore.ListTaskItemsAsync(projectId, ct),
            Cells = await _runStore.ListCellsAsync(projectId, ct)
        };
    }

    public async Task<BenchmarkCsvExportQueryResult> GetCsvAsync(Guid projectId, CancellationToken ct)
    {
        var project = await _projectStore.GetProjectAsync(projectId, ct) ?? throw new BenchmarkNotFoundException("Benchmark project was not found.");

        var page = await _runStore.ListAllRunsAsync(projectId, ct);
        var pairwiseFit = await _pairwiseStore.GetActivePairwiseFitAsync(projectId, ct);
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

        var attempt = await _judgeStore.GetJudgeAttemptAsync(attemptId, ct);
        return BenchmarkJudgeSerialization.DeserializeResult(attempt?.ResultJson);
    }
}
