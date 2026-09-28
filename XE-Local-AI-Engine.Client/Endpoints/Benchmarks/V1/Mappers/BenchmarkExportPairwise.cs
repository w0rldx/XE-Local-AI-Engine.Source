namespace XE_Local_AI_Engine.Client.Endpoints.Benchmarks.V1.Mappers;

using XE_Local_AI_Engine.Client.Persistence.Stores;

internal static class BenchmarkExportPairwise
{
    /// <summary>
    ///     The fit's per-run entries, indexed by run. The scores arrive already normalized by
    ///     <see cref="BenchmarkPairwiseFitScores" />: an unreadable blob is empty, null entries are gone and a run named
    ///     twice keeps its first entry, so every run appears once.
    /// </summary>
    public static IReadOnlyDictionary<Guid, BenchmarkPairwiseScoreEntry> ByRun(IReadOnlyList<BenchmarkPairwiseScoreEntry> scores) =>
        scores.ToDictionary(static entry => entry.RunId);
}
