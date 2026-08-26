namespace XE_Local_AI_Engine.Tests.Benchmarks;

using XE_Local_AI_Engine.Client.Services.Benchmarks;
using XE_Local_AI_Engine.Tests.Testing;

public sealed class BenchmarkPairedBootstrapTests
{
    [Test]
    public void Estimate_KnownDifferences_MatchesTheReferenceInterval()
    {
        // Deltas {0, 0, 30}. The paired resample draws 3 of these 3 with replacement, so the replicate mean is
        // exactly enumerable: 0 with probability 8/27, 10 with 12/27, 20 with 6/27, 30 with 1/27. The nearest-rank
        // 2.5 percentile therefore sits at 0 (8/27 = 0.296 > 0.025) and the 97.5 percentile at 30 (the mass at or
        // below 20 is 26/27 = 0.963 < 0.975). Hand-computable, hence the fixture.
        var estimate = AssertEx.NotNull(BenchmarkPairedBootstrap.Estimate([70, 70, 100], [70, 70, 70]));

        AssertEx.Equal(3, estimate.SharedItemCount);
        AssertEx.Equal(10d, estimate.Delta);
        AssertEx.Equal(0d, estimate.CiLow);
        AssertEx.Equal(30d, estimate.CiHigh);

        // 0 is inside [0, 30]: a suite whose whole difference rests on one item has not separated the two.
        AssertEx.False(estimate.Separated, "an interval touching zero is not a separation");
    }

    [Test]
    public void Estimate_AConstantAdvantageOnEveryItem_IsADegenerateInterval_AndSeparates()
    {
        // Every delta is +6, so every resample of them means +6 whatever it draws. This is the one case where the
        // interval is exact rather than estimated, and it is what "separated" is supposed to look like.
        var estimate = AssertEx.NotNull(BenchmarkPairedBootstrap.Estimate([76, 66, 86, 56], [70, 60, 80, 50]));

        AssertEx.Equal(6d, estimate.Delta);
        AssertEx.Equal(6d, estimate.CiLow);
        AssertEx.Equal(6d, estimate.CiHigh);
        AssertEx.True(estimate.Separated, "an interval that never reaches zero separates the two cells");
    }

    [Test]
    public void Estimate_TwoIdenticalCells_IsZeroWithAZeroWidthInterval_AndDoesNotSeparate()
    {
        var estimate = AssertEx.NotNull(BenchmarkPairedBootstrap.Estimate([70, 80, 90], [70, 80, 90]));

        AssertEx.Equal(0d, estimate.Delta);
        AssertEx.Equal(0d, estimate.CiLow);
        AssertEx.Equal(0d, estimate.CiHigh);
        AssertEx.False(estimate.Separated, "0 is inside [0, 0]");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public void Estimate_FewerThanThreeSharedItems_ReportsNoInterval(int shared)
    {
        var a = Enumerable.Repeat(90, shared).ToArray();
        var b = Enumerable.Repeat(10, shared).ToArray();

        // An 80-point gap, and still no interval: three points cannot support one, and the absence is the answer the
        // reader gets instead of a number nobody should trust.
        AssertEx.Null(BenchmarkPairedBootstrap.Estimate(a, b));
    }

    [Test]
    public void Estimate_IsDeterministicUnderItsSeed()
    {
        int[] a = [83, 51, 77, 64, 91, 40, 72];
        int[] b = [70, 62, 55, 68, 74, 58, 61];

        var first = AssertEx.NotNull(BenchmarkPairedBootstrap.Estimate(a, b));
        var second = BenchmarkPairedBootstrap.Estimate(a, b);

        AssertEx.Equal(first, second);
    }

    [Test]
    public void Estimate_IntervalBracketsTheMeanAndStaysInsideTheObservedDeltas()
    {
        // A resample mean is a convex combination of the deltas, so no percentile of it can leave their range, and
        // the point estimate is one of the achievable means. True of any correct paired bootstrap, at any seed.
        int[] a = [83, 51, 77, 64, 91, 40, 72];
        int[] b = [70, 62, 55, 68, 74, 58, 61];
        var deltas = a.Zip(b, static (left, right) => (double)(left - right)).ToArray();

        var estimate = AssertEx.NotNull(BenchmarkPairedBootstrap.Estimate(a, b));

        AssertEx.True(estimate.CiLow <= estimate.Delta && estimate.Delta <= estimate.CiHigh, "the interval must bracket the point estimate");
        AssertEx.True(estimate.CiLow >= deltas.Min(), "no resample mean can fall below the smallest observed delta");
        AssertEx.True(estimate.CiHigh <= deltas.Max(), "no resample mean can exceed the largest observed delta");
    }

    [Test]
    public void Estimate_IsPaired_NotTwoIndependentResamples()
    {
        // The pairing is the whole point: two cells that alternate wins item by item have a mean delta of 0 but a
        // wide interval, while a constant small advantage has a narrow one. Independent resampling would not tell
        // these apart, because both have the same marginal distributions on each side.
        var alternating = AssertEx.NotNull(BenchmarkPairedBootstrap.Estimate([100, 0, 100, 0, 100, 0], [0, 100, 0, 100, 0, 100]));
        var constant = AssertEx.NotNull(BenchmarkPairedBootstrap.Estimate([51, 51, 51, 51, 51, 51], [50, 50, 50, 50, 50, 50]));

        AssertEx.Equal(0d, alternating.Delta);
        AssertEx.False(alternating.Separated, "an even split of wins is not a separation");
        AssertEx.True(constant.Separated, "a one-point advantage held on every item IS a separation");
        AssertEx.True(alternating.CiHigh - alternating.CiLow > constant.CiHigh - constant.CiLow,
            "the coin-flip suite must produce the wider interval, even though its mean delta is smaller");
    }

    [Test]
    public void Estimate_MismatchedVectorLengths_IsRejected()
    {
        AssertEx.Throws<ArgumentException>(() => _ = BenchmarkPairedBootstrap.Estimate([70, 80, 90], [70, 80]));
    }
}
