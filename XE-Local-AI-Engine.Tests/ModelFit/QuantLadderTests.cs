namespace XE_Local_AI_Engine.Tests.ModelFit;

using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="QuantLadder" /> quality ordering + floor, and the I-quant densities added to
///     <see cref="MemoryFitEstimator.BytesPerWeight" /> so IQ files are sized at their true bits-per-weight instead of the
///     legacy 4.5bpw default.
/// </summary>
public sealed class QuantLadderTests
{
    [Test]
    public void QualityRank_OrdersHigherQualityBeforeLower()
    {
        // Lower rank == better quality.
        AssertEx.True(QuantLadder.QualityRank("Q8_0") < QuantLadder.QualityRank("Q4_K_M"), "Q8_0 must outrank Q4_K_M.");
        AssertEx.True(QuantLadder.QualityRank("Q4_K_M") < QuantLadder.QualityRank("Q3_K_M"), "Q4_K_M must outrank Q3_K_M.");
        AssertEx.True(QuantLadder.QualityRank("Q3_K_M") < QuantLadder.QualityRank("Q2_K"), "Q3_K_M must outrank Q2_K.");
        AssertEx.True(QuantLadder.QualityRank("IQ4_XS") < QuantLadder.QualityRank("Q3_K_M"), "IQ4_XS must outrank Q3_K_M.");
    }

    [Test]
    public void QualityRank_IsCaseInsensitive()
    {
        AssertEx.Equal(QuantLadder.QualityRank("Q4_K_M"), QuantLadder.QualityRank("q4_k_m"));
    }

    [Test]
    public void QualityRank_UnknownLabel_RanksJustBelowQ4KM()
    {
        // An unknown/off-ladder label is treated as just below Q4_K_M (the estimator's conservative default density), so
        // it still meets the floor and never crowds out a genuinely higher-quality known quant.
        AssertEx.True(QuantLadder.QualityRank("totally-made-up") > QuantLadder.QualityRank("Q4_K_M"),
            "an unknown quant must rank below Q4_K_M.");
        AssertEx.True(QuantLadder.MeetsFloor("totally-made-up"), "an unknown quant must still meet the floor.");
    }

    [Test]
    public void MeetsFloor_AcceptsAtOrAboveFloor_RejectsBelow()
    {
        AssertEx.True(QuantLadder.MeetsFloor("Q4_K_M"), "Q4_K_M is above the floor.");
        AssertEx.True(QuantLadder.MeetsFloor("IQ4_XS"), "IQ4_XS is above the floor.");
        AssertEx.True(QuantLadder.MeetsFloor("Q3_K_M"), "Q3_K_M IS the floor.");
        AssertEx.False(QuantLadder.MeetsFloor("Q3_K_S"), "Q3_K_S is below the floor.");
        AssertEx.False(QuantLadder.MeetsFloor("IQ3_M"), "IQ3_M is below the floor.");
        AssertEx.False(QuantLadder.MeetsFloor("Q2_K"), "Q2_K is below the floor.");
        AssertEx.False(QuantLadder.MeetsFloor("IQ2_M"), "IQ2_M is below the floor.");
    }

    [Test]
    public void QualityRank_DynamicPrefix_PricesOffStrippedBase()
    {
        // An Unsloth Dynamic (UD-) token ranks as its stripped base, so the advisor orders it correctly.
        AssertEx.Equal(QuantLadder.QualityRank("Q4_K_M"), QuantLadder.QualityRank("UD-Q4_K_M"));
    }

    [Test]
    public void TierOf_ReturnsLadderTier_OnLadder_AndNull_OffLadder()
    {
        // The single-source-of-truth tier the download picker (GgufQuantQuality) reads.
        AssertEx.True(QuantLadder.TierOf("Q4_K_M") == GgufQuantTier.Balanced, "Q4_K_M is Balanced.");
        AssertEx.True(QuantLadder.TierOf("IQ4_XS") == GgufQuantTier.Small, "IQ4_XS is graded Small for the picker.");
        AssertEx.True(QuantLadder.TierOf("Q8_0") == GgufQuantTier.NearLossless, "Q8_0 is NearLossless.");
        // An off-ladder _L variant has no ladder tier — the classifier falls back to its own rules.
        AssertEx.True(QuantLadder.TierOf("Q4_K_L") is null, "off-ladder Q4_K_L has no ladder tier.");
    }

    [Test]
    public void BytesPerWeight_IQuants_UseDistinctDensities_NotTheDefault()
    {
        var q4 = MemoryFitEstimator.BytesPerWeight("Q4_K_M");

        // IQ4_XS (4.4597bpw) is denser-packed than Q4_K_M (4.5bpw) — proving it is NOT falling through to the 4.5 default.
        AssertEx.True(MemoryFitEstimator.BytesPerWeight("IQ4_XS") < q4, "IQ4_XS must size below Q4_K_M.");
        // IQ2_M (2.93bpw) must be far below Q4_K_M.
        AssertEx.True(MemoryFitEstimator.BytesPerWeight("IQ2_M") < MemoryFitEstimator.BytesPerWeight("Q3_K_M"),
            "IQ2_M must size below Q3_K_M.");
        // A descending sanity chain across the I-quant range.
        AssertEx.True(MemoryFitEstimator.BytesPerWeight("IQ3_M") > MemoryFitEstimator.BytesPerWeight("IQ2_M"),
            "IQ3_M must size above IQ2_M.");
        AssertEx.True(MemoryFitEstimator.BytesPerWeight("IQ2_M") > MemoryFitEstimator.BytesPerWeight("IQ1_S"),
            "IQ2_M must size above IQ1_S.");
    }
}
