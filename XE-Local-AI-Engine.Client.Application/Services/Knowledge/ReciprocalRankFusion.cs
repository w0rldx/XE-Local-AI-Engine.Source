namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Default <see cref="IRankingFusionService" />. Implements Reciprocal Rank Fusion: for each chunk, the base fused
///     score is the sum, over every list the chunk appears in, of <c>1 / (k + rank)</c> where <c>rank</c> is the chunk's
///     1-based position in that list. The rank-smoothing constant <c>k = 60</c> is the value from the original RRF paper
///     (Cormack et al., 2009); it damps the influence of top ranks so no single arm dominates the fusion.
///     <para>
///         <see cref="FuseScored" /> adds an OPTIONAL score-aware tilt on top of that base. Classic RRF discards the arm
///         scores, so a barely-relevant rank-1 hit fuses identically to a strong one. The tilt fixes that WITHOUT
///         reintroducing the scale problem RRF exists to solve (BM25 magnitude and cosine similarity live on incomparable
///         scales): each arm's scores are min-max normalized WITHIN the arm to <c>[0, 1]</c>, then the RRF contribution is
///         multiplied by <c>1 + weight * normalizedScore</c>. Because the tilt is multiplicative on the RRF term, it stays
///         on the RRF scale (no additive blow-up), reduces to EXACT pure RRF at <c>weight = 0</c> or when an arm carries no
///         usable score spread, and only ever re-orders entries whose ranks the arm's own magnitudes disagree about.
///     </para>
///     Stateless and deterministic — safe as a singleton.
/// </summary>
public sealed class ReciprocalRankFusion : IRankingFusionService
{
    /// <summary>Rank-smoothing constant from the original RRF paper. A larger value flattens the contribution of top ranks.</summary>
    public const int K = 60;

    public IReadOnlyList<RankFusionEntry> Fuse(IReadOnlyList<IReadOnlyList<Guid>> rankedLists)
    {
        ArgumentNullException.ThrowIfNull(rankedLists);

        // Project the id-only lists onto scored arms with a placeholder score, then run the shared core with the score
        // tilt disabled (Rrf). This is exactly the classic RRF the score-aware path degrades to, so the two paths cannot
        // drift apart.
        var arms = new List<IReadOnlyList<RankFusionInput>?>(rankedLists.Count);
        foreach (var list in rankedLists)
        {
            arms.Add(list?.Select(static id => new RankFusionInput(id, 0d)).ToList());
        }

        return FuseScored(arms, RankFusionStrategy.Rrf, scoreWeight: 0d);
    }

    public IReadOnlyList<RankFusionEntry> FuseScored(IReadOnlyList<IReadOnlyList<RankFusionInput>?> arms,
        RankFusionStrategy strategy,
        double scoreWeight)
    {
        ArgumentNullException.ThrowIfNull(arms);

        // Only a positive weight under the score-aware strategy tilts anything; everything else is pure RRF (tilt == 1).
        var weight = strategy == RankFusionStrategy.ScoreAware ? Math.Max(0d, scoreWeight) : 0d;
        var applyTilt = weight > 0d;

        var scores = new Dictionary<Guid, double>();
        foreach (var arm in arms)
        {
            if (arm is null || arm.Count == 0)
            {
                continue;
            }

            // Normalize this arm's scores to [0, 1] ONLY when the tilt is active AND the arm carries a usable spread; a
            // constant/single/non-finite arm yields the neutral normalizer (every entry maps to 0 → tilt of exactly 1 →
            // pure RRF for that arm), so a degenerate score column never distorts the rank-based order.
            var normalizer = applyTilt ? ArmScoreNormalizer.ForArm(arm) : ArmScoreNormalizer.Neutral;

            for (var position = 0; position < arm.Count; position++)
            {
                var entry = arm[position];

                // Rank is 1-based, so the best entry in a list contributes the largest reciprocal.
                var rank = position + 1;
                var rrf = 1d / (K + rank);
                var tilt = 1d + (weight * normalizer.Normalize(entry.Score));
                var contribution = rrf * tilt;

                scores[entry.ChunkId] = scores.TryGetValue(entry.ChunkId, out var running)
                    ? running + contribution
                    : contribution;
            }
        }

        return scores
               .OrderByDescending(pair => pair.Value)
               .ThenBy(pair => pair.Key)
               .Select(pair => new RankFusionEntry(pair.Key, pair.Value))
               .ToList();
    }

    /// <summary>
    ///     Per-arm min-max score normalizer. <see cref="Normalize" /> maps a raw arm score into <c>[0, 1]</c> where the
    ///     arm's strongest score is 1 and its weakest is 0. When the arm has no usable spread — fewer than two entries, a
    ///     constant score column, or any non-finite bound — every score maps to <c>0</c> (the neutral tilt), so
    ///     normalization can never turn a degenerate score column into a divide-by-zero or an arbitrary re-ordering.
    /// </summary>
    private readonly struct ArmScoreNormalizer
    {
        private readonly double _min;
        private readonly double _range;
        private readonly bool _usable;

        private ArmScoreNormalizer(double min, double range, bool usable)
        {
            _min = min;
            _range = range;
            _usable = usable;
        }

        /// <summary>A normalizer that maps every score to 0 — i.e. no tilt, pure RRF.</summary>
        public static ArmScoreNormalizer Neutral => new(min: 0d, range: 0d, usable: false);

        public static ArmScoreNormalizer ForArm(IReadOnlyList<RankFusionInput> arm)
        {
            var min = double.PositiveInfinity;
            var max = double.NegativeInfinity;
            foreach (var score in arm.Select(static entry => entry.Score))
            {
                if (!double.IsFinite(score))
                {
                    // A non-finite score poisons min/max; treat the whole arm as carrying no usable spread rather than
                    // producing NaN tilts.
                    return Neutral;
                }

                if (score < min)
                {
                    min = score;
                }

                if (score > max)
                {
                    max = score;
                }
            }

            var range = max - min;
            // A single entry (min == max) or an all-equal column carries no discriminating magnitude: degrade to neutral.
            if (arm.Count < 2 || !double.IsFinite(range) || range <= 0d)
            {
                return Neutral;
            }

            return new ArmScoreNormalizer(min, range, usable: true);
        }

        public double Normalize(double score)
        {
            if (!_usable)
            {
                return 0d;
            }

            // Clamp defends against a score outside the observed [min, max] (it never is for the arm it was built from,
            // but keeps the contract total).
            var normalized = (score - _min) / _range;
            return Math.Clamp(normalized, 0d, 1d);
        }
    }
}
