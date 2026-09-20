namespace XE_Local_AI_Engine.Client.Services.Knowledge;

using System.Runtime.InteropServices;

/// <summary>
///     Merges several independently ranked chunk lists — the lexical FTS arm and the semantic vector arm — into a single
///     ranking via Reciprocal Rank Fusion.
/// </summary>
/// <remarks>Pure and deterministic, with no database access, so it is unit-testable in isolation.</remarks>
public interface IRankingFusionService
{
    /// <summary>
    ///     Fuses the given ranked lists, each ordered best-first, into one ranking ordered by descending fused score.
    /// </summary>
    /// <remarks>
    ///     The result is the union of every chunk id that appears in any list. This is the classic, score-AGNOSTIC
    ///     Reciprocal Rank Fusion: only the rank position of each id is used.
    /// </remarks>
    IReadOnlyList<RankFusionEntry> Fuse(IReadOnlyList<IReadOnlyList<Guid>> rankedLists);

    /// <summary>
    ///     Fuses ranked arms carrying a per-entry relevance score into one ranking ordered by descending fused score.
    /// </summary>
    /// <remarks>
    ///     The result is the union of every chunk id in any arm. <see cref="RankFusionStrategy.Rrf" /> IGNORES the scores
    ///     and reproduces pure RRF over the same id order; <see cref="RankFusionStrategy.ScoreAware" /> tilts each rank's
    ///     contribution by its arm-normalized score. Strategies: <c>docs/wiki/15-knowledge-base.md</c> ("Hybrid retrieval").
    /// </remarks>
    /// <param name="arms">Ranked arms, best-first; each <see cref="RankFusionInput.Score" /> is oriented so HIGHER means more relevant, and a null arm is skipped.</param>
    /// <param name="strategy">Whether to apply the score tilt (<see cref="RankFusionStrategy.ScoreAware" />) or ignore it (<see cref="RankFusionStrategy.Rrf" />).</param>
    /// <param name="scoreWeight">
    ///     Maximum multiplicative tilt applied to an arm's top-normalized entry; clamped non-negative, <c>0</c> reduces
    ///     to pure RRF, ignored under <see cref="RankFusionStrategy.Rrf" />.
    /// </param>
    IReadOnlyList<RankFusionEntry> FuseScored(IReadOnlyList<IReadOnlyList<RankFusionInput>?> arms,
        RankFusionStrategy strategy,
        double scoreWeight);
}

/// <summary>Which fusion is applied to the default (no-reranker) retrieval path.</summary>
public enum RankFusionStrategy
{
    /// <summary>Classic score-agnostic Reciprocal Rank Fusion: rank position only.</summary>
    Rrf = 0,

    /// <summary>Score-aware fusion: per-arm min-max normalized scores tilt the RRF contribution.</summary>
    ScoreAware = 1
}

/// <summary>One scored input to fusion: a chunk id and its per-arm relevance score (higher means more relevant).</summary>
/// <param name="ChunkId">The chunk identifier.</param>
/// <param name="Score">The arm-local relevance score, oriented so higher ranks higher (the caller normalizes orientation).</param>
[StructLayout(LayoutKind.Auto)]
public readonly record struct RankFusionInput(Guid ChunkId, double Score);

/// <summary>One fused entry: a chunk id and its accumulated Reciprocal Rank Fusion score (higher ranks higher).</summary>
public sealed class RankFusionEntry
{
    /// <summary>The chunk identifier.</summary>
    public required Guid ChunkId { get; init; }

    /// <summary>The summed RRF score across every input list the chunk appeared in.</summary>
    public required double Score { get; init; }
}
