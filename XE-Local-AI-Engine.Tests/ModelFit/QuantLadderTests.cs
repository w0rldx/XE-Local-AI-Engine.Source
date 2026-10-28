namespace XE_Local_AI_Engine.Tests.ModelFit;

using XE_Local_AI_Engine.Client.Services.ModelFit.Fit;
using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     <see cref="QuantLadder" /> quality ordering + floor, and the I-quant densities added to
///     <see cref="MemoryFitEstimator.BytesPerWeight" /> so IQ files are sized at their true bits-per-weight instead of the
///     legacy 4.5bpw default.
/// </summary>
[Category(TestCategories.Unit)]
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
    public void IsNativeFormat_TrueForNativeFp4Formats_FalseForRequantizableQuants()
    {
        // MXFP4 (gpt-oss's native ~4.25bpw MoE format) and NVFP4 (NVIDIA Blackwell-era FP4) are the native,
        // non-requantizable quants today — re-quantizing either UP to Q6/Q8 wastes space without adding quality.
        AssertEx.True(QuantLadder.IsNativeFormat("MXFP4"), "MXFP4 is a native format.");
        AssertEx.True(QuantLadder.IsNativeFormat("mxfp4"), "IsNativeFormat is case-insensitive.");
        AssertEx.True(QuantLadder.IsNativeFormat("NVFP4"), "NVFP4 is a native format.");
        AssertEx.True(QuantLadder.IsNativeFormat("nvfp4"), "IsNativeFormat is case-insensitive for NVFP4 too.");
        AssertEx.False(QuantLadder.IsNativeFormat("Q4_K_M"), "Q4_K_M is a requantizable quant, not native.");
        AssertEx.False(QuantLadder.IsNativeFormat("Q8_0"), "Q8_0 is a requantizable quant, not native.");
    }

    [Test]
    public void QualityRank_NativeFp4_PassesTheRecommendedQualityGate()
    {
        // The "Recommended" gate is QualityRank(quant) <= QualityRank(Q4_K_M); an off-ladder label ranks ONE step
        // past it, so without NVFP4/MXFP4 on the ladder a native-FP4 repo is demoted to "Can run" regardless.
        var gate = QuantLadder.QualityRank(MemoryFitEstimator.DefaultQuant);

        AssertEx.True(QuantLadder.QualityRank("NVFP4") <= gate, "NVFP4 must clear the recommended-quality gate.");
        AssertEx.True(QuantLadder.QualityRank("MXFP4") <= gate, "MXFP4 must clear the recommended-quality gate.");
        AssertEx.True(QuantLadder.QualityRank("NVFP4") < QuantLadder.QualityRank("totally-made-up"),
            "a native format must not share the off-ladder unknown rank.");
        AssertEx.True(QuantLadder.MeetsFloor("NVFP4"), "NVFP4 is well above the auto-recommend quality floor.");
        AssertEx.True(QuantLadder.MeetsFloor("MXFP4"), "MXFP4 is well above the auto-recommend quality floor.");
    }

    [Test]
    public void QualityRank_NativeFp4_SitsBetweenQ5KSAndQ4KM()
    {
        // Native FP4 is trained precision, not a lossy requant, so it outranks Q4_K_M despite sizing narrower than it —
        // the same rank-vs-bytes divergence the IQ4 family shows. NVFP4 leads MXFP4 on scale granularity.
        AssertEx.True(QuantLadder.QualityRank("Q5_K_S") < QuantLadder.QualityRank("NVFP4"),
            "the 5-bit K-quants still outrank native FP4.");
        AssertEx.True(QuantLadder.QualityRank("NVFP4") < QuantLadder.QualityRank("MXFP4"),
            "NVFP4's finer scale granularity ranks it above MXFP4 at the same density.");
        AssertEx.True(QuantLadder.QualityRank("MXFP4") < QuantLadder.QualityRank("Q4_K_M"),
            "a native 4-bit format outranks a lossy 4-bit requant.");
    }

    [Test]
    public void TierOf_NativeFp4_IsOnLadderAndBadgesBalanced()
    {
        // Off-ladder returned null here, which sent the download picker to its family fallback instead of a real tier.
        AssertEx.True(QuantLadder.TierOf("NVFP4") == GgufQuantTier.Balanced, "NVFP4 badges Balanced.");
        AssertEx.True(QuantLadder.TierOf("MXFP4") == GgufQuantTier.Balanced, "MXFP4 badges Balanced.");
        AssertEx.True(QuantLadder.TierOf("nvfp4") == GgufQuantTier.Balanced, "TierOf is case-insensitive for NVFP4.");
    }

    [Test]
    public void BytesPerWeight_NvFp4_MatchesMxFp4NativeDensity()
    {
        // Measured, not theoretical: s-batman/Ornith-1.0-9B-NVFP4-MTP-GGUF ships MXFP4 and NVFP4 conversions of the
        // same model from the same converter at byte-identical sizes (5.45 GB each).
        AssertEx.Equal(MemoryFitEstimator.BytesPerWeight("MXFP4"), MemoryFitEstimator.BytesPerWeight("NVFP4"));
        AssertEx.True(MemoryFitEstimator.BytesPerWeight("NVFP4") < MemoryFitEstimator.BytesPerWeight("Q4_K_M"),
            "NVFP4 must not fall through to the 4.5bpw unknown-quant default.");
    }

    [Test]
    public void BytesPerWeight_MxFp4_UsesNativeDensity()
    {
        // MXFP4 is ~4.25 bits/weight — below the 4.5bpw unknown-quant default, so it is NOT falling through to it.
        AssertEx.Equal(4.25d / 8d, MemoryFitEstimator.BytesPerWeight("MXFP4"));
        AssertEx.True(MemoryFitEstimator.BytesPerWeight("MXFP4") < MemoryFitEstimator.BytesPerWeight("Q4_K_M"),
            "MXFP4 must size below the 4.5bpw Q4_K_M default.");
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

    [Test]
    public void QualityRank_UnslothXlRungs_SitDirectlyAboveTheirBaseAndBelowTheNextRungUp()
    {
        string[][] cases =
        [
            ["Q8_K_XL", "F16", "Q8_0"],
            ["Q6_K_XL", "Q8_0", "Q6_K"],
            ["Q5_K_XL", "Q6_K", "Q5_K_M"],
            ["Q4_K_XL", "MXFP4", "Q4_K_M"],
            ["Q3_K_XL", "IQ4_XS", "Q3_K_L"],
            ["Q2_K_XL", "IQ3_XXS", "Q2_K"]
        ];

        foreach (var rungs in cases)
        {
            var xl = rungs[0];
            var above = rungs[1];
            var below = rungs[2];
            AssertEx.True(QuantLadder.QualityRank(above) < QuantLadder.QualityRank(xl), $"{xl} must rank below {above}.");
            AssertEx.Equal(QuantLadder.QualityRank(below) - 1, QuantLadder.QualityRank(xl));
        }
    }

    [Test]
    public void CeilingRank_AdmitsTheXlSiblingDirectlyAboveTheCeiling_AndNothingElse()
    {
        // A Q4_K_M ceiling admits UD-Q4_K_XL (same nominal bit-width) but still excludes MXFP4 and every Q5 rung.
        AssertEx.Equal(QuantLadder.QualityRank("Q4_K_XL"), QuantLadder.CeilingRank("Q4_K_M"));
        AssertEx.True(QuantLadder.QualityRank("MXFP4") < QuantLadder.CeilingRank("Q4_K_M"), "MXFP4 stays above a Q4_K_M ceiling.");
        AssertEx.Equal(QuantLadder.QualityRank("Q8_K_XL"), QuantLadder.CeilingRank("Q8_0"));
        // A ceiling whose upper neighbor is not an _XL rung is unchanged.
        AssertEx.Equal(QuantLadder.QualityRank("Q4_K_S"), QuantLadder.CeilingRank("Q4_K_S"));
        AssertEx.Equal(QuantLadder.QualityRank("F32"), QuantLadder.CeilingRank("F32"));
        // An _XL ceiling admits itself only.
        AssertEx.Equal(QuantLadder.QualityRank("Q4_K_XL"), QuantLadder.CeilingRank("UD-Q4_K_XL"));
    }

    [Test]
    public void UnslothQ4KXl_DynamicPrefixRanksAsBase_IsBalanced_AndClearsTheRecommendedGate()
    {
        AssertEx.Equal(QuantLadder.QualityRank("Q4_K_XL"), QuantLadder.QualityRank("UD-Q4_K_XL"));
        AssertEx.True(QuantLadder.TierOf("Q4_K_XL") == GgufQuantTier.Balanced, "Q4_K_XL is Balanced.");
        AssertEx.True(QuantLadder.QualityRank("UD-Q4_K_XL") <= QuantLadder.QualityRank(MemoryFitEstimator.DefaultQuant),
            "UD-Q4_K_XL must clear the Q4_K_M recommended gate.");
    }
}
