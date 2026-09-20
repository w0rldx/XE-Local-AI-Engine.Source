namespace XE_Local_AI_Engine.Client.Services.Knowledge;

/// <summary>
///     Default <see cref="IRankingFusionService" />: a chunk's base fused score is the sum, over every list it appears
///     in, of <c>1 / (k + rank)</c> with <c>rank</c> its 1-based position in that list.
/// </summary>
/// <remarks>
///     The rank-smoothing constant <c>k = 60</c> is the value from the original RRF paper (Cormack et al., 2009); it damps
///     the influence of top ranks so no single arm dominates. <see cref="FuseScored" /> adds an optional tilt: the RRF
///     contribution is multiplied by <c>1 + weight * normalizedScore</c>, each arm's scores being min-max normalized WITHIN
///     the arm. Multiplicative on the RRF term, the tilt stays on the RRF scale, reduces to exact pure RRF at
///     <c>weight = 0</c> or on an arm with no usable spread, and re-orders only entries the magnitudes disagree about.
/// </remarks>
public sealed class ReciprocalRankFusion : IRankingFusionService
{
    /// <summary>Rank-smoothing constant from the original RRF paper. A larger value flattens the contribution of top ranks.</summary>
    public const int K = 60;

    public IReadOnlyList<RankFusionEntry> Fuse(IReadOnlyList<IReadOnlyList<Guid>> rankedLists)
    {
        ArgumentNullException.ThrowIfNull(rankedLists);

        // Project the id-only lists onto scored arms with a placeholder score, then run the shared core with the tilt
        // disabled (Rrf) — exactly the classic RRF the score-aware path degrades to, so the two paths cannot drift apart.
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
            // constant, single or non-finite arm gets the neutral normalizer (every entry 0, tilt 1, so pure RRF for it).
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
               .Select(pair => new RankFusionEntry { ChunkId = pair.Key, Score = pair.Value })
               .ToList();
    }

    /// <summary>
    ///     Per-arm min-max score normalizer: <see cref="Normalize" /> maps a raw arm score into <c>[0, 1]</c>, the arm's
    ///     strongest score at 1 and its weakest at 0.
    /// </summary>
    /// <remarks>
    ///     With no usable spread — fewer than two entries, a constant score column, or any non-finite bound — every score
    ///     maps to <c>0</c>, the neutral tilt, so a degenerate score column can never become a divide-by-zero or an
    ///     arbitrary re-ordering.
    /// </remarks>
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
