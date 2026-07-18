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
    ///     result is the union of every chunk id that appears in any list. This is the classic, score-AGNOSTIC Reciprocal
    ///     Rank Fusion: only the rank position of each id is used.
    /// </summary>
    IReadOnlyList<RankFusionEntry> Fuse(IReadOnlyList<IReadOnlyList<Guid>> rankedLists);

    /// <summary>
    ///     Fuses the given ranked arms (each ordered best-first) carrying a per-entry relevance score, into one ranking
    ///     ordered by descending fused score. The result is the union of every chunk id that appears in any arm.
    ///     <para>
    ///         With <see cref="RankFusionStrategy.Rrf" /> the per-entry scores are IGNORED and the result is byte-identical
    ///         to <see cref="Fuse(System.Collections.Generic.IReadOnlyList{System.Collections.Generic.IReadOnlyList{System.Guid}})" />
    ///         over the same id order — pure RRF, kept as the graceful fallback and comparison baseline.
    ///     </para>
    ///     <para>
    ///         With <see cref="RankFusionStrategy.ScoreAware" /> each arm's scores are min-max normalized WITHIN the arm
    ///         (so incomparable scales — BM25 magnitude vs cosine — never blend directly) and used to TILT the RRF
    ///         contribution multiplicatively by up to <paramref name="scoreWeight" />, so a rank whose arm score is far
    ///         above the arm's floor outranks an equally-ranked but marginal competitor. It degrades to pure RRF for any
    ///         arm whose scores are empty, single, constant, or non-finite (normalization carries no signal there).
    ///     </para>
    /// </summary>
    /// <param name="arms">
    ///     The ranked arms, each ordered best-first. Each entry's <see cref="RankFusionInput.Score" /> is a relevance value
    ///     oriented so that HIGHER means more relevant (the caller orients incomparable raw scores — e.g. negating FTS5
    ///     BM25, which is more-negative-for-stronger). A null arm is skipped.
    /// </param>
    /// <param name="strategy">Whether to apply the score tilt (<see cref="RankFusionStrategy.ScoreAware" />) or ignore it (<see cref="RankFusionStrategy.Rrf" />).</param>
    /// <param name="scoreWeight">
    ///     The maximum multiplicative tilt applied to an arm's top-normalized entry under
    ///     <see cref="RankFusionStrategy.ScoreAware" /> (clamped to be non-negative; <c>0</c> reduces to pure RRF). Ignored
    ///     under <see cref="RankFusionStrategy.Rrf" />.
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
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Auto)]
public readonly record struct RankFusionInput(Guid ChunkId, double Score);

/// <summary>One fused entry: a chunk id and its accumulated Reciprocal Rank Fusion score (higher ranks higher).</summary>
/// <param name="ChunkId">The chunk identifier.</param>
/// <param name="Score">The summed RRF score across every input list the chunk appeared in.</param>
public sealed record RankFusionEntry(Guid ChunkId, double Score);
