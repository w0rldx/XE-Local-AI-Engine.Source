namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Merges several independently ranked chunk lists (for example the lexical FTS arm and the semantic vector arm) into a
///     single ranking via Reciprocal Rank Fusion. Pure and deterministic — no database access — so it is unit-testable in
///     isolation.
/// </summary>
public interface IRankingFusionService
{
    /// <summary>
    ///     Fuses the given ranked lists (each ordered best-first) into one ranking, ordered by descending fused score. The
    ///     result is the union of every chunk id that appears in any list.
    /// </summary>
    IReadOnlyList<RankFusionEntry> Fuse(IReadOnlyList<IReadOnlyList<Guid>> rankedLists);
}

/// <summary>One fused entry: a chunk id and its accumulated Reciprocal Rank Fusion score (higher ranks higher).</summary>
/// <param name="ChunkId">The chunk identifier.</param>
/// <param name="Score">The summed RRF score across every input list the chunk appeared in.</param>
public sealed record RankFusionEntry(Guid ChunkId, double Score);
