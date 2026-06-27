namespace XE_Local_AI_Engine.Tests.Providers.HuggingFace;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;
using XE_Local_AI_Engine.Tests.Testing;

/// <summary>
///     Quality-tier classification of GGUF quant tokens: each named tier, Unsloth Dynamic (UD-) tokens pricing off their
///     stripped base, and the total/never-throw contract (unknown or blank → the safe <see cref="GgufQuantTier.Balanced" />
///     middle).
/// </summary>
public sealed class GgufQuantQualityTests
{
    [Test]
    [Arguments("Q8_0")]
    [Arguments("Q6_K")]
    [Arguments("Q6_K_L")]
    [Arguments("F16")]
    [Arguments("FP16")]
    [Arguments("BF16")]
    [Arguments("F32")]
    [Arguments("FP32")]
    [Arguments("F64")]
    public void Classify_NearLosslessQuants(string quant)
    {
        AssertEx.Equal(GgufQuantTier.NearLossless, GgufQuantQuality.Classify(quant));
    }

    [Test]
    [Arguments("Q5_K_M")]
    [Arguments("Q5_K_S")]
    [Arguments("Q5_K_L")]
    public void Classify_SweetSpotQuants(string quant)
    {
        AssertEx.Equal(GgufQuantTier.SweetSpot, GgufQuantQuality.Classify(quant));
    }

    [Test]
    [Arguments("Q4_K_M")]
    [Arguments("Q4_K_S")]
    [Arguments("Q4_K_L")]
    public void Classify_BalancedQuants(string quant)
    {
        AssertEx.Equal(GgufQuantTier.Balanced, GgufQuantQuality.Classify(quant));
    }

    [Test]
    [Arguments("Q3_K_S")]
    [Arguments("Q3_K_M")]
    [Arguments("Q3_K_L")]
    [Arguments("Q3_K_XL")]
    [Arguments("IQ3_XXS")]
    [Arguments("IQ3_M")]
    [Arguments("IQ4_XS")]
    [Arguments("IQ4_NL")]
    [Arguments("Q4_0")]
    [Arguments("Q4_1")]
    [Arguments("Q5_0")]
    [Arguments("Q5_1")]
    [Arguments("Q4_0_4_4")]
    [Arguments("Q4_0_8_8")]
    public void Classify_SmallQuants(string quant)
    {
        AssertEx.Equal(GgufQuantTier.Small, GgufQuantQuality.Classify(quant));
    }

    [Test]
    [Arguments("Q2_K")]
    [Arguments("IQ1_S")]
    [Arguments("IQ1_M")]
    [Arguments("IQ2_XXS")]
    [Arguments("IQ2_M")]
    public void Classify_MinimalQuants(string quant)
    {
        AssertEx.Equal(GgufQuantTier.Minimal, GgufQuantQuality.Classify(quant));
    }

    [Test]
    [Arguments("UD-Q6_K", GgufQuantTier.NearLossless)]
    [Arguments("UD-Q5_K_M", GgufQuantTier.SweetSpot)]
    [Arguments("UD-Q4_K_M", GgufQuantTier.Balanced)]
    [Arguments("UD_Q4_K_XL", GgufQuantTier.Balanced)] // underscore-separated marker still stripped; Q4_K_XL → Balanced
    [Arguments("UD-IQ2_M", GgufQuantTier.Minimal)]
    public void Classify_DynamicQuant_PricesOffStrippedBase(string quant, GgufQuantTier expected)
    {
        AssertEx.Equal(expected, GgufQuantQuality.Classify(quant));
    }

    [Test]
    [Arguments("q4_k_m", GgufQuantTier.Balanced)] // lowercase normalized
    [Arguments("q6_k", GgufQuantTier.NearLossless)]
    public void Classify_NormalizesCasing(string quant, GgufQuantTier expected)
    {
        AssertEx.Equal(expected, GgufQuantQuality.Classify(quant));
    }

    [Test]
    [Arguments("not-a-quant")]
    [Arguments("Q9_Z")]
    [Arguments("")]
    [Arguments("   ")]
    public void Classify_UnknownOrBlank_DefaultsToBalanced(string quant)
    {
        AssertEx.Equal(GgufQuantTier.Balanced, GgufQuantQuality.Classify(quant));
    }

    [Test]
    public void Classify_Null_DefaultsToBalanced()
    {
        AssertEx.Equal(GgufQuantTier.Balanced, GgufQuantQuality.Classify(null));
    }
}
