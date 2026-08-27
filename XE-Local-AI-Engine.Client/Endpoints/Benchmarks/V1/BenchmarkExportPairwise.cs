namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1;

using System.Text.Json;
using XE_Local_AI_Engine.Client.Persistence.Stores;

internal static class BenchmarkExportPairwise
{
    /// <summary>Same options the store wrote the blob with, so a member name cannot bind on one side only.</summary>
    private static readonly JsonSerializerOptions ScoreOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    ///     The fit's per-run entries, indexed by run. Non-throwing: a blob that cannot be parsed exports as no
    ///     pairwise columns rather than failing the whole download, exactly as a bad snapshot does for llama-bench.
    /// </summary>
    public static IReadOnlyDictionary<Guid, BenchmarkPairwiseScoreEntry> Scores(BenchmarkPairwiseFitRecord? fit)
    {
        if (fit is null)
        {
            return new Dictionary<Guid, BenchmarkPairwiseScoreEntry>();
        }

        try
        {
            var entries = JsonSerializer.Deserialize<BenchmarkPairwiseScoreEntry[]>(fit.ScoresJson, ScoreOptions) ?? [];
            return entries.GroupBy(static entry => entry.RunId).ToDictionary(static group => group.Key, static group => group.First());
        }
        catch (JsonException)
        {
            return new Dictionary<Guid, BenchmarkPairwiseScoreEntry>();
        }
    }
}
