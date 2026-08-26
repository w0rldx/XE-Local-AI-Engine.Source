namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>
///     One pairwise verdict, already normalized to its canonical unordered pair: <paramref name="RunAId" /> is the
///     smaller GUID and <paramref name="Verdict" /> says which of the two won regardless of which side the judge was
///     shown first.
/// </summary>
/// <param name="Verdict"><c>a</c>, <c>b</c> or <c>tie</c>.</param>
public sealed record BenchmarkPairwiseVerdict(Guid RunAId, Guid RunBId, string Verdict);

/// <summary>One run's place in a fit: its mapped 0..100 strength, its bootstrap interval, and how it was counted.</summary>
/// <param name="Score">
///     <see langword="null" /> when this run is in the fit's scope but carries no publishable strength — too few
///     verdicts, or a minority component of a disconnected comparison graph. <paramref name="Reason" /> says which.
/// </param>
/// <param name="BootstrapAppearances">
///     How many bootstrap replicates this run was drawn into. Below <see cref="BenchmarkBradleyTerry.MinimumBootstrapAppearances" />
///     the interval is withheld rather than reported from a handful of replicates.
/// </param>
public sealed record BenchmarkPairwiseRunScore(
    Guid RunId,
    int? Score,
    int? CiLow,
    int? CiHigh,
    int Comparisons,
    int BootstrapAppearances,
    string? Reason);

/// <summary>
///     The outcome of fitting one comparison set. A <paramref name="Refusal" /> is a fit that produced no strengths at
///     all — it is published as such, so the reason reaches the ranking read without it having to re-read verdicts.
/// </summary>
public sealed record BenchmarkBradleyTerryFit(
    IReadOnlyList<BenchmarkPairwiseRunScore> Scores,
    int Iterations,
    int Replicates,
    string? Refusal = null);

/// <summary>
///     Regularized Bradley–Terry over a cohort's pairwise verdicts, with a cluster bootstrap for the intervals. No
///     dependency: the MM update is a dozen lines and the prior is one constant.
/// </summary>
/// <remarks>
///     Two properties are load-bearing and neither is a hope. A symmetric <see cref="PriorPseudoCount" /> pseudo-count
///     on every pair that was actually compared gives every participating run a win total strictly above zero, so
///     complete separation — a run that wins everything, or loses everything — still has a finite unique maximum
///     instead of driving a log to negative infinity. And the bootstrap resamples whole UNORDERED PAIRS: the two
///     presentation orders of one pair are the same observation measured twice to cancel position bias, so splitting
///     them across replicates would reintroduce exactly the bias the swap exists to remove.
/// </remarks>
public static class BenchmarkBradleyTerry
{
    /// <summary>Jeffreys/Beta(½,½) pseudo-count per compared pair — the paired-design equivalent of Firth's correction.</summary>
    public const double PriorPseudoCount = 0.5;

    /// <summary>Hitting this refuses the fit. With the prior, convergence is guaranteed; the cap is a safety net.</summary>
    public const int MaximumIterations = 500;

    public const double ConvergenceTolerance = 1e-10;

    public const int DefaultReplicates = 1000;

    /// <summary>Fewer bootstrap appearances than this withholds the interval rather than reporting a fragile one.</summary>
    public const int MinimumBootstrapAppearances = 200;

    /// <summary>A run with fewer verdicts than this has nothing to estimate a strength from.</summary>
    public const int MinimumVerdicts = 2;

    /// <summary>The whole fit refused because the MM sweep did not converge inside <see cref="MaximumIterations" />.</summary>
    public const string RefusalUnfitted = "pairwise-unfitted";

    /// <summary>This run has no publishable strength, though the fit itself succeeded.</summary>
    public const string ReasonInsufficient = "pairwise-insufficient";

    public const string VerdictA = "a";
    public const string VerdictB = "b";
    public const string VerdictTie = "tie";

    /// <summary>
    ///     Fits the verdicts and bootstraps an interval for every run that gets a strength. Runs outside the largest
    ///     connected component are returned with no score: cross-component strengths are not comparable, and averaging
    ///     them would invent an ordering the verdicts never established.
    /// </summary>
    /// <param name="replicates">Bootstrap replicates; 0 fits without intervals (the fixtures' fast path).</param>
    /// <param name="maximumIterations">
    ///     The MM sweep cap. Defaults to <see cref="MaximumIterations" /> and is a parameter only so the
    ///     refuse-on-non-convergence branch is reachable from a test — with the prior in place, no real cohort can
    ///     reach 500 sweeps, and a branch nothing can exercise is a branch nothing pins.
    /// </param>
    public static BenchmarkBradleyTerryFit Fit(IReadOnlyList<BenchmarkPairwiseVerdict> verdicts,
        int replicates = DefaultReplicates,
        int maximumIterations = MaximumIterations)
    {
        ArgumentNullException.ThrowIfNull(verdicts);
        var runs = verdicts.SelectMany(static verdict => new[] { verdict.RunAId, verdict.RunBId }).Distinct().Order().ToArray();
        if (runs.Length < 2)
        {
            return new BenchmarkBradleyTerryFit([.. runs.Select(static run => new BenchmarkPairwiseRunScore(run, null, null, null, 0, 0, ReasonInsufficient))],
                Iterations: 0,
                Replicates: 0);
        }

        var indexByRun = runs.Select(static (run, index) => (run, index)).ToDictionary(static entry => entry.run, static entry => entry.index);
        var pairs = Aggregate(verdicts, indexByRun);
        var component = LargestComponent(pairs, runs.Length);
        var fitted = pairs.Where(pair => component.Contains(pair.IndexA)).ToArray();
        var solution = Solve(fitted, runs.Length, maximumIterations);
        if (solution is null)
        {
            return new BenchmarkBradleyTerryFit([.. runs.Select(static run => new BenchmarkPairwiseRunScore(run, null, null, null, 0, 0, RefusalUnfitted))],
                maximumIterations,
                Replicates: 0,
                RefusalUnfitted);
        }

        var counts = new int[runs.Length];
        foreach (var pair in pairs)
        {
            counts[pair.IndexA] += pair.Total;
            counts[pair.IndexB] += pair.Total;
        }

        var scores = MapScores(solution.LogStrengths, component);
        var intervals = replicates > 0 ? Bootstrap(fitted, runs.Length, component, replicates, maximumIterations) : [];
        return new BenchmarkBradleyTerryFit([.. runs.Select((run, index) => ToRunScore(run, index, scores, counts, intervals, component))],
            solution.Iterations,
            replicates);
    }

    private static BenchmarkPairwiseRunScore ToRunScore(Guid run,
        int index,
        IReadOnlyDictionary<int, int> scores,
        IReadOnlyList<int> counts,
        IReadOnlyDictionary<int, BootstrapSample> intervals,
        IReadOnlySet<int> component)
    {
        if (!component.Contains(index) || counts[index] < MinimumVerdicts || !scores.TryGetValue(index, out var score))
        {
            return new BenchmarkPairwiseRunScore(run, null, null, null, counts[index], 0, ReasonInsufficient);
        }

        if (!intervals.TryGetValue(index, out var sample) || sample.Scores.Count < MinimumBootstrapAppearances)
        {
            return new BenchmarkPairwiseRunScore(run, score, null, null, counts[index], sample?.Scores.Count ?? 0, null);
        }

        var ordered = sample.Scores.Order().ToArray();
        return new BenchmarkPairwiseRunScore(run, score, Percentile(ordered, 0.025), Percentile(ordered, 0.975), counts[index], ordered.Length, null);
    }

    /// <summary>
    ///     Collapses the ordered verdicts into one aggregate per unordered pair. A tie contributes 0.5 to each side and
    ///     1 to the total — Rao–Kupper's fitted tie threshold is deliberately not built: it estimates a third parameter
    ///     from far fewer tie observations than a 12-run cohort produces, and the 0.5 split is what the published
    ///     arena fits use.
    /// </summary>
    private static PairAggregate[] Aggregate(IReadOnlyList<BenchmarkPairwiseVerdict> verdicts, IReadOnlyDictionary<Guid, int> indexByRun)
    {
        var byPair = new Dictionary<(int A, int B), (double WinsA, double WinsB, int Total)>();
        foreach (var verdict in verdicts)
        {
            var key = (indexByRun[verdict.RunAId], indexByRun[verdict.RunBId]);
            _ = byPair.TryGetValue(key, out var current);
            var (creditA, creditB) = verdict.Verdict switch
            {
                VerdictA => (1.0, 0.0),
                VerdictB => (0.0, 1.0),
                _ => (0.5, 0.5)
            };
            byPair[key] = (current.WinsA + creditA, current.WinsB + creditB, current.Total + 1);
        }

        return [.. byPair.OrderBy(static entry => entry.Key.A).ThenBy(static entry => entry.Key.B)
                         .Select(static entry => new PairAggregate(entry.Key.A, entry.Key.B, entry.Value.WinsA, entry.Value.WinsB, entry.Value.Total))];
    }

    /// <summary>
    ///     The runs of the largest connected component of the comparison graph, tie-broken by the lowest run index so
    ///     the choice is deterministic rather than dictionary-order.
    /// </summary>
    private static HashSet<int> LargestComponent(IReadOnlyList<PairAggregate> pairs, int runCount)
    {
        var parent = Enumerable.Range(0, runCount).ToArray();
        foreach (var pair in pairs)
        {
            var rootA = Find(parent, pair.IndexA);
            var rootB = Find(parent, pair.IndexB);
            if (rootA != rootB)
            {
                parent[rootB] = rootA;
            }
        }

        var members = new Dictionary<int, List<int>>();
        foreach (var pair in pairs)
        {
            foreach (var index in (int[])[pair.IndexA, pair.IndexB])
            {
                var root = Find(parent, index);
                if (!members.TryGetValue(root, out var list))
                {
                    list = [];
                    members[root] = list;
                }

                if (!list.Contains(index))
                {
                    list.Add(index);
                }
            }
        }

        var largest = members.Values.OrderByDescending(static list => list.Count).ThenBy(static list => list.Min()).FirstOrDefault();
        return largest is null ? [] : [.. largest];
    }

    private static int Find(int[] parent, int index)
    {
        while (parent[index] != index)
        {
            parent[index] = parent[parent[index]];
            index = parent[index];
        }

        return index;
    }

    /// <summary>
    ///     The MM sweep of Hunter (2004) with the pseudo-count prior folded into both the win totals and the pair
    ///     counts. Returns <see langword="null" /> when the cap is reached, which refuses the fit — a half-converged
    ///     number published as a score is worse than no score.
    /// </summary>
    private static Solution? Solve(IReadOnlyList<PairAggregate> pairs, int runCount, int maximumIterations)
    {
        if (pairs.Count == 0)
        {
            return new Solution(new double[runCount], Iterations: 0);
        }

        var wins = new double[runCount];
        foreach (var pair in pairs)
        {
            wins[pair.IndexA] += pair.WinsA + PriorPseudoCount;
            wins[pair.IndexB] += pair.WinsB + PriorPseudoCount;
        }

        var strengths = new double[runCount];
        Array.Fill(strengths, 1.0);
        var previous = new double[runCount];
        for (var iteration = 1; iteration <= maximumIterations; iteration++)
        {
            Array.Copy(strengths, previous, runCount);
            var denominators = new double[runCount];
            foreach (var pair in pairs)
            {
                var total = pair.Total + (2 * PriorPseudoCount);
                var shared = total / (previous[pair.IndexA] + previous[pair.IndexB]);
                denominators[pair.IndexA] += shared;
                denominators[pair.IndexB] += shared;
            }

            var sum = 0.0;
            for (var index = 0; index < runCount; index++)
            {
                strengths[index] = denominators[index] > 0 ? wins[index] / denominators[index] : previous[index];
                sum += strengths[index];
            }

            for (var index = 0; index < runCount; index++)
            {
                strengths[index] /= sum;
            }

            var delta = 0.0;
            for (var index = 0; index < runCount; index++)
            {
                if (strengths[index] > 0 && previous[index] > 0)
                {
                    delta = Math.Max(delta, Math.Abs(Math.Log(strengths[index]) - Math.Log(previous[index])));
                }
            }

            if (delta < ConvergenceTolerance)
            {
                return new Solution([.. strengths.Select(static strength => strength > 0 ? Math.Log(strength) : double.NegativeInfinity)], iteration);
            }
        }

        return null;
    }

    /// <summary>
    ///     Maps log-strengths to the existing 0..100 projection as the estimated probability of beating an AVERAGE
    ///     opponent in this cohort. Deliberately not <c>100·p/max(p)</c>, which would pin the winner at 100 forever and
    ///     reintroduce the saturation the pairwise mode exists to remove.
    /// </summary>
    private static Dictionary<int, int> MapScores(IReadOnlyList<double> logStrengths, IReadOnlySet<int> component)
    {
        if (component.Count == 0)
        {
            return [];
        }

        var mean = component.Average(index => logStrengths[index]);
        return component.ToDictionary(static index => index,
            index => (int)Math.Round(100 / (1 + Math.Exp(-(logStrengths[index] - mean))), MidpointRounding.AwayFromZero));
    }

    /// <summary>
    ///     Cluster bootstrap: the resampling unit is the unordered pair, drawn with both its ordered verdicts. Seeded
    ///     at 0, so two runs over the same verdicts report the same interval.
    /// </summary>
    private static Dictionary<int, BootstrapSample> Bootstrap(IReadOnlyList<PairAggregate> pairs,
        int runCount,
        IReadOnlySet<int> component,
        int replicates,
        int maximumIterations)
    {
        var samples = new Dictionary<int, BootstrapSample>();
        if (pairs.Count == 0)
        {
            return samples;
        }

#pragma warning disable S2245 // A bootstrap must be reproducible: the seed IS the point, and no security decision reads it.
        var random = new Random(0);
#pragma warning restore S2245
        for (var replicate = 0; replicate < replicates; replicate++)
        {
            var drawn = new Dictionary<(int A, int B), PairAggregate>();
            for (var draw = 0; draw < pairs.Count; draw++)
            {
                var pair = pairs[random.Next(pairs.Count)];
                var key = (pair.IndexA, pair.IndexB);
                drawn[key] = drawn.TryGetValue(key, out var existing)
                    ? existing with
                    {
                        WinsA = existing.WinsA + pair.WinsA,
                        WinsB = existing.WinsB + pair.WinsB,
                        Total = existing.Total + pair.Total
                    }
                    : pair;
            }

            var replicatePairs = drawn.Values.OrderBy(static pair => pair.IndexA).ThenBy(static pair => pair.IndexB).ToArray();
            var replicateComponent = LargestComponent(replicatePairs, runCount);
            var solution = Solve(replicatePairs, runCount, maximumIterations);
            if (solution is null)
            {
                // A degenerate resample is dropped, not fatal: the point estimate already converged, and refusing the
                // whole fit over one replicate would hide an interval the other 999 can report.
                continue;
            }

            foreach (var (index, score) in MapScores(solution.LogStrengths, replicateComponent))
            {
                if (!component.Contains(index))
                {
                    continue;
                }

                if (!samples.TryGetValue(index, out var sample))
                {
                    sample = new BootstrapSample([]);
                    samples[index] = sample;
                }

                sample.Scores.Add(score);
            }
        }

        return samples;
    }

    /// <summary>Nearest-rank percentile over the replicate scores a run actually appeared in.</summary>
    private static int Percentile(IReadOnlyList<int> ordered, double quantile)
    {
        var rank = (int)Math.Ceiling(quantile * ordered.Count) - 1;
        return ordered[Math.Clamp(rank, 0, ordered.Count - 1)];
    }

    private sealed record PairAggregate(int IndexA, int IndexB, double WinsA, double WinsB, int Total);

    private sealed record Solution(IReadOnlyList<double> LogStrengths, int Iterations);

    private sealed record BootstrapSample(List<int> Scores);
}
