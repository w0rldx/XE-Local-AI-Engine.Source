namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Benchmarks;

internal static class BenchmarkExportStatistics
{
    /// <summary>
    ///     One entry per repeat group, plus one per ungrouped run. Warm-ups are dropped — absorbing the first-launch
    ///     cost is their whole job, and averaging them back in would put the cost right back into the numbers the
    ///     repeats after them exist to isolate.
    /// </summary>
    public static IReadOnlyList<BenchmarkExportRepeatGroupResponse> Groups(IReadOnlyList<BenchmarkRunRecord> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        return runs.Where(static run => !run.IsWarmup && run.Throughput is not null)
                   .GroupBy(static run => run.RepeatGroupId ?? run.Id)
                   .Select(static group =>
                   {
                       var ordered = group.OrderBy(static run => run.RepeatIndex ?? 0).ThenBy(static run => run.CreatedAtUtc).ToArray();
                       return new BenchmarkExportRepeatGroupResponse
                       {
                           RepeatGroupId = ordered[0].RepeatGroupId,
                           ModelName = ordered[0].PrimaryModelName,
                           RepeatMode = ordered[0].RepeatMode,
                           RunIds = [.. ordered.Select(static run => run.Id)],
                           MeanPromptTokens = Summarize(ordered.Select(static run => (double?)run.Throughput?.PromptTokens)).Mean,
                           MeanGenerationTokens = Summarize(ordered.Select(static run => (double?)run.Throughput?.GenerationTokens)).Mean,
                           TtftMs = Summarize(ordered.Select(static run => run.Throughput?.TtftMs)),
                           PromptTokensPerSecond = Summarize(ordered.Select(static run => run.Throughput?.PromptTokensPerSecond)),
                           GenerationTokensPerSecond = Summarize(ordered.Select(static run => run.Throughput?.GenerationTokensPerSecond))
                       };
                   })
                   .ToArray();
    }

    /// <summary>
    ///     Mean and POPULATION standard deviation. The runs are the population — this is every measurement that was
    ///     taken, not a draw from a larger set — and the sample form would divide by zero on the single run that is by
    ///     far the commonest group size.
    /// </summary>
    public static BenchmarkExportSampleStatisticsResponse Summarize(IEnumerable<double?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var samples = values.Where(static value => value is not null).Select(static value => value!.Value).ToArray();
        if (samples.Length == 0)
        {
            return new BenchmarkExportSampleStatisticsResponse();
        }

        var mean = samples.Average();
        var variance = samples.Sum(sample => (sample - mean) * (sample - mean)) / samples.Length;
        return new BenchmarkExportSampleStatisticsResponse
        {
            SampleCount = samples.Length,
            Mean = mean,
            StdDev = Math.Sqrt(variance),
            Samples = samples
        };
    }

    /// <summary>
    ///     Two rows per group in llama-bench's own shape: a prompt-processing row and a token-generation row.
    ///     <para>
    ///         The model and runtime facts come from the group's FIRST run. An answer-variance group has one snapshot
    ///         per repeat rather than one shared snapshot — the seed differs — but every fact read here (model file,
    ///         its size, the GPU-layer count, the runtime build and the host GPUs) comes from the parts that do not,
    ///         because the whole group is frozen against one launch. The token COUNTS are group means, not the first
    ///         run's: those genuinely vary across an answer-variance group.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<BenchmarkExportLlamaBenchRowResponse> LlamaBenchRows(IReadOnlyList<BenchmarkExportRepeatGroupResponse> groups,
        IReadOnlyDictionary<Guid, BenchmarkExportRunFacts> facts)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(facts);
        var rows = new List<BenchmarkExportLlamaBenchRowResponse>(groups.Count * 2);
        foreach (var group in groups)
        {
            var runFacts = facts.TryGetValue(group.RunIds[0], out var found) ? found : BenchmarkExportRunFacts.Empty;
            rows.Add(Row(group, runFacts, group.PromptTokensPerSecond, nPrompt: Round(group.MeanPromptTokens), nGen: 0));
            rows.Add(Row(group, runFacts, group.GenerationTokensPerSecond, nPrompt: 0, nGen: Round(group.MeanGenerationTokens)));
        }

        return rows;
    }

    private static BenchmarkExportLlamaBenchRowResponse Row(BenchmarkExportRepeatGroupResponse group,
        BenchmarkExportRunFacts facts,
        BenchmarkExportSampleStatisticsResponse statistics,
        int nPrompt,
        int nGen) =>
        new()
        {
            BuildCommit = facts.BuildCommit,
            GpuInfo = facts.GpuInfo,
            ModelFilename = facts.ModelFilename,
            ModelSize = facts.ModelSizeBytes,
            NGpuLayers = facts.GpuLayers,
            NPrompt = nPrompt,
            NGen = nGen,
            AvgTs = statistics.Mean,
            StddevTs = statistics.StdDev,
            Samples = statistics.SampleCount,
            RepeatGroupId = group.RepeatGroupId,
            ModelName = group.ModelName
        };

    private static int Round(double? value) =>
        value is { } number && number > 0 ? (int)Math.Round(number, MidpointRounding.AwayFromZero) : 0;
}
