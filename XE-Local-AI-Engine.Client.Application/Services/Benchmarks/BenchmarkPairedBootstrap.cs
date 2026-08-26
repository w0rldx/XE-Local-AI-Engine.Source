namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>
///     The paired difference between two measurement cells over the items they SHARE, with a percentile bootstrap
///     interval around it. <paramref name="Separated" /> is false exactly when 0 lies inside the interval — which is
///     the sentence B3 exists to let a reader say: "these two are not separated by this suite".
/// </summary>
/// <param name="SharedItemCount">How many items were rankable in BOTH cells; the resampling unit is one of these.</param>
/// <param name="Delta">Mean of <c>qualityA − qualityB</c> over the shared items. Positive means A scored higher.</param>
public sealed record BenchmarkPairedDelta(int SharedItemCount, double Delta, double CiLow, double CiHigh, bool Separated);

/// <summary>
///     Paired-difference bootstrap over per-item quality scores (B3). Pure and read-time: nothing here is stored, and
///     the whole estimate is recomputed from the cell table on every compare.
/// </summary>
/// <remarks>
///     The resampling unit is the ITEM, drawn with BOTH cells' scores for it — that is what makes the interval a
///     paired one. Resampling the two cells independently would throw away the pairing and widen the interval by the
///     between-item variance the suite deliberately holds constant.
/// </remarks>
public static class BenchmarkPairedBootstrap
{
    /// <summary>
    ///     Below three shared items no interval is reported at all. Two points cannot support one, and an interval
    ///     nobody should trust is worse than the absence a reader can see (plan §7.4).
    /// </summary>
    public const int MinimumSharedItems = 3;

    public const int DefaultReplicates = 2000;

    /// <summary>
    ///     The paired delta and its 95 % percentile interval, or <see langword="null" /> when fewer than
    ///     <see cref="MinimumSharedItems" /> items are shared. <paramref name="qualityA" /> and
    ///     <paramref name="qualityB" /> are the two cells' scores for the SAME items, in the same order.
    /// </summary>
    public static BenchmarkPairedDelta? Estimate(IReadOnlyList<int> qualityA, IReadOnlyList<int> qualityB, int replicates = DefaultReplicates)
    {
        ArgumentNullException.ThrowIfNull(qualityA);
        ArgumentNullException.ThrowIfNull(qualityB);
        if (qualityA.Count != qualityB.Count)
        {
            throw new ArgumentException("Paired quality vectors must be the same length.", nameof(qualityB));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(replicates, 1);
        if (qualityA.Count < MinimumSharedItems)
        {
            return null;
        }

        var deltas = new double[qualityA.Count];
        for (var index = 0; index < deltas.Length; index++)
        {
            deltas[index] = qualityA[index] - (double)qualityB[index];
        }

        var means = new double[replicates];
        // Seeded `Random`, exactly as BenchmarkBradleyTerry does: the seeded constructor keeps the legacy sequence for
        // compatibility, and nothing here is stored anyway — a hypothetical runtime change would move a DISPLAYED
        // interval, not a persisted score or an input hash. (The NIAH generator makes the opposite call for the
        // opposite reason: its bytes feed an item's input hash.)
#pragma warning disable S2245 // A bootstrap must be reproducible: the seed IS the point, and no security decision reads it.
        var random = new Random(0);
#pragma warning restore S2245
        for (var replicate = 0; replicate < replicates; replicate++)
        {
            var sum = 0d;
            for (var draw = 0; draw < deltas.Length; draw++)
            {
                sum += deltas[random.Next(deltas.Length)];
            }

            means[replicate] = sum / deltas.Length;
        }

        Array.Sort(means);
        var low = Percentile(means, 0.025);
        var high = Percentile(means, 0.975);
        return new BenchmarkPairedDelta(deltas.Length,
            deltas.Average(),
            low,
            high,

            // Separated means the interval stays on one side of zero. An interval that touches zero is not separated:
            // a delta of exactly 0 is inside [0, 0], which is the all-equal case and the clearest "no difference".
            low > 0 || high < 0);
    }

    /// <summary>Nearest-rank percentile, matching <see cref="BenchmarkBradleyTerry" />'s so two intervals agree.</summary>
    private static double Percentile(IReadOnlyList<double> ordered, double quantile)
    {
        var rank = (int)Math.Ceiling(quantile * ordered.Count) - 1;
        return ordered[Math.Clamp(rank, 0, ordered.Count - 1)];
    }
}
