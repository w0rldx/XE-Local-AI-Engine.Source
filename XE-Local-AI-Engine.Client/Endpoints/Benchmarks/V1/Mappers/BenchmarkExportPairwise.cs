namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;

using XE_Local_AI_Engine.Client.Persistence.Stores;

internal static class BenchmarkExportPairwise
{
    /// <summary>
    ///     The fit's per-run entries, indexed by run; the first entry wins for a run named twice. The scores arrive
    ///     already parsed: an unreadable blob is empty here, so it exports as no pairwise columns.
    /// </summary>
    public static IReadOnlyDictionary<Guid, BenchmarkPairwiseScoreEntry> ByRun(IReadOnlyList<BenchmarkPairwiseScoreEntry> scores) =>
        scores.GroupBy(static entry => entry.RunId).ToDictionary(static group => group.Key, static group => group.First());
}
