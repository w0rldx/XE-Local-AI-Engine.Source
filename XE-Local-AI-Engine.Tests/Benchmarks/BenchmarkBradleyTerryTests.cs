namespace XE_Local_AI_Engine.Tests.Benchmarks;

using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class BenchmarkBradleyTerryTests
{
    private static readonly Guid RunA = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid RunB = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RunC = new("33333333-3333-3333-3333-333333333333");
    private static readonly Guid RunD = new("44444444-4444-4444-4444-444444444444");
    private static readonly Guid RunE = new("55555555-5555-5555-5555-555555555555");

    [Test]
    public void Fit_TwoRunSweep_MatchesTheHandComputedRegularizedStrengths()
    {
        // w_A = 2 + a = 2.5, w_B = 0 + a = 0.5, ñ = 2 + 2a = 3 with a = 0.5, so the stationary point is
        // p = (5/6, 1/6) and the mapped score is 100·σ(±½·ln 5) = 69 / 31. Hand-computable, hence the fixture.
        var fit = BenchmarkBradleyTerry.Fit(Sweep(RunA, RunB), replicates: 0);

        AssertEx.Null(fit.Refusal);
        AssertEx.Equal(69, Score(fit, RunA));
        AssertEx.Equal(31, Score(fit, RunB));
        AssertEx.Equal(2, fit.Scores.Single(score => score.RunId == RunA).Comparisons);
    }

    [Test]
    public void Fit_TransitiveThreeRunTournament_OrdersThemAndKeepsTheSymmetricMiddleAt50()
    {
        // A > B > C, every pair judged in both orders. The tournament is symmetric under reversing A and C, so the
        // middle run must sit exactly at the cohort mean and the outer two must be mirror images of each other.
        var fit = BenchmarkBradleyTerry.Fit([.. Sweep(RunA, RunB), .. Sweep(RunA, RunC), .. Sweep(RunB, RunC)], replicates: 0);

        AssertEx.Null(fit.Refusal);
        AssertEx.True(Score(fit, RunA) > Score(fit, RunB), "the run that beat both must outrank the middle");
        AssertEx.True(Score(fit, RunB) > Score(fit, RunC), "the middle must outrank the run that lost both");
        AssertEx.Equal(50, Score(fit, RunB));
        AssertEx.Equal(100, Score(fit, RunA) + Score(fit, RunC));
    }

    [Test]
    public void Fit_AllTies_MapsEveryRunTo50()
    {
        var fit = BenchmarkBradleyTerry.Fit([.. Tied(RunA, RunB), .. Tied(RunA, RunC), .. Tied(RunB, RunC)], replicates: 0);

        AssertEx.True(fit.Scores.All(static score => score.Score == 50), "a field nothing separated must sit flat at 50");
    }

    [Test]
    public void Fit_RunWinsEverything_ProducesAFiniteScoreWithNoLogOfZero()
    {
        var fit = BenchmarkBradleyTerry.Fit([.. Sweep(RunA, RunB), .. Sweep(RunA, RunC), .. Tied(RunB, RunC)], replicates: 0);

        AssertEx.Null(fit.Refusal);
        // Complete separation: without the prior the MLE does not exist here at all.
        AssertEx.True(Score(fit, RunA) is > 50 and < 100, "an unbeaten run is bounded away from 100 by the prior");
        AssertEx.True(fit.Scores.All(static score => score.Score is >= 0 and <= 100), "every mapped score stays inside 0..100");
    }

    [Test]
    public void Fit_RunLosesEverything_ProducesAFinitePositiveScore()
    {
        var fit = BenchmarkBradleyTerry.Fit([.. Sweep(RunA, RunC), .. Sweep(RunB, RunC), .. Tied(RunA, RunB)], replicates: 0);

        AssertEx.Null(fit.Refusal);
        AssertEx.True(Score(fit, RunC) is > 0 and < 50, "a run that never won is bounded away from 0 by the prior");
    }

    [Test]
    public void Fit_EveryFixture_ConvergesWellUnderTheIterationCap()
    {
        var fit = BenchmarkBradleyTerry.Fit([.. Sweep(RunA, RunB), .. Sweep(RunA, RunC), .. Sweep(RunB, RunC), .. Sweep(RunA, RunD)], replicates: 0);

        // Measured 62 sweeps for this four-run fixture at the 1e-10 log-strength tolerance. The bound is what the cap
        // has to be a safety net for, not a tuning target: the plan's "< 60" was an estimate taken before the fit ran.
        AssertEx.True(fit.Iterations is > 0 and < 100, $"expected convergence well under the 500-sweep cap, took {fit.Iterations}");
    }

    [Test]
    public void Fit_NonConvergentSweep_RefusesTheWholeFitInsteadOfPublishingAHalfFitNumber()
    {
        var fit = BenchmarkBradleyTerry.Fit([.. Sweep(RunA, RunB), .. Sweep(RunA, RunC)], replicates: 0, maximumIterations: 1);

        AssertEx.Equal(BenchmarkBradleyTerry.RefusalUnfitted, fit.Refusal);
        AssertEx.True(fit.Scores.All(static score => score.Score is null), "a refused fit publishes no score at all");
    }

    [Test]
    public void Fit_DisconnectedGraph_PublishesTheLargestComponentOnly()
    {
        // A-B-C form one component and D-E another. Strengths from two components are not on one scale, so only the
        // larger is published — averaging across them would invent an ordering the verdicts never established.
        var fit = BenchmarkBradleyTerry.Fit([.. Sweep(RunA, RunB), .. Sweep(RunB, RunC), .. Sweep(RunC, RunA), .. Sweep(RunD, RunE)], replicates: 0);

        AssertEx.True(fit.Scores.Where(score => score.RunId != RunD && score.RunId != RunE).All(static score => score.Score is not null),
            "the largest component is published");
        foreach (var stranded in fit.Scores.Where(score => score.RunId == RunD || score.RunId == RunE))
        {
            AssertEx.Null(stranded.Score);
            AssertEx.Equal(BenchmarkBradleyTerry.ReasonInsufficient, stranded.Reason);
        }
    }

    [Test]
    public void Bootstrap_ResamplesUnorderedPairs_AndNeverSplitsASwapPair()
    {
        // One pair, one verdict each way: the position swap cancelled out. Because the resampling unit is the PAIR,
        // every replicate redraws that 1-1 split whole and the interval collapses onto 50. Resampling individual
        // verdicts would draw 2-0 replicates and report a wide interval — the position bias back in the CI.
        var fit = BenchmarkBradleyTerry.Fit([new BenchmarkPairwiseVerdict(RunA, RunB, "a"), new BenchmarkPairwiseVerdict(RunA, RunB, "b")]);

        var run = fit.Scores.Single(score => score.RunId == RunA);
        AssertEx.Equal(50, run.Score);
        AssertEx.Equal(50, run.CiLow);
        AssertEx.Equal(50, run.CiHigh);
    }

    [Test]
    public void Bootstrap_RunAbsentFromAReplicate_IsSkippedRatherThanScoredZero()
    {
        var fit = BenchmarkBradleyTerry.Fit([.. Sweep(RunA, RunB), .. Sweep(RunB, RunC), .. Sweep(RunC, RunD)]);

        var run = fit.Scores.Single(score => score.RunId == RunD);
        AssertEx.True(run.BootstrapAppearances < BenchmarkBradleyTerry.DefaultReplicates,
            "a chain end is drawn out of some replicates, so it cannot appear in all of them");
        AssertEx.True(run.CiLow is null or > 0, "an absent run contributes nothing to a replicate rather than a zero");
    }

    [Test]
    public void Bootstrap_IsDeterministicUnderSeedZero()
    {
        BenchmarkPairwiseVerdict[] verdicts = [.. Sweep(RunA, RunB), .. Sweep(RunA, RunC), .. Sweep(RunB, RunC)];

        var first = BenchmarkBradleyTerry.Fit(verdicts);
        var second = BenchmarkBradleyTerry.Fit(verdicts);

        AssertEx.Equal(BenchmarkCanonicalJson.Serialize(first), BenchmarkCanonicalJson.Serialize(second));
    }

    [Test]
    public void Fit_RunWithASingleVerdict_ReportsInsufficientRatherThanAStrength()
    {
        var fit = BenchmarkBradleyTerry.Fit([.. Sweep(RunA, RunB), new BenchmarkPairwiseVerdict(RunA, RunC, "a")], replicates: 0);

        var thin = fit.Scores.Single(score => score.RunId == RunC);
        AssertEx.Equal(BenchmarkBradleyTerry.ReasonInsufficient, thin.Reason);
        AssertEx.Null(thin.Score);
    }

    private static int? Score(BenchmarkBradleyTerryFit fit, Guid runId) =>
        fit.Scores.Single(score => score.RunId == runId).Score;

    /// <summary>Both presentation orders of one pair, both won by the first run.</summary>
    private static BenchmarkPairwiseVerdict[] Sweep(Guid winner, Guid loser) =>
        winner.CompareTo(loser) < 0
                ? [new BenchmarkPairwiseVerdict(winner, loser, "a"), new BenchmarkPairwiseVerdict(winner, loser, "a")]
                : [new BenchmarkPairwiseVerdict(loser, winner, "b"), new BenchmarkPairwiseVerdict(loser, winner, "b")];

    private static BenchmarkPairwiseVerdict[] Tied(Guid first, Guid second) =>
        [new BenchmarkPairwiseVerdict(first, second, "tie"), new BenchmarkPairwiseVerdict(first, second, "tie")];
}
