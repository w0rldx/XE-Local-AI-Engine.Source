namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     The single source of truth for GGUF quant quality: a llama.cpp quant ladder ordered by quality (best → worst),
///     where each known token carries BOTH a fine-grained quality RANK and the coarse <see cref="GgufQuantTier" /> grade.
///     <para>
///         Two consumers read different facets of the same table:
///         <list type="bullet">
///             <item>the advisor (memory-fit) walks the fine <see cref="QualityRank" /> + <see cref="DefaultFloorQuant" />
///             to step down to the highest quant that fits;</item>
///             <item><see cref="GgufQuantQuality" /> (the download picker) reads <see cref="TierOf" /> for the coarse
///             per-row badge.</item>
///         </list>
///         Keeping both facets in one table means the quant knowledge is defined once. Quality is NOT a strict function
///         of bytes-per-weight across families (an I-quant beats a same-bit K-quant), so the order is the curated quality
///         ranking; <see cref="MemoryFitEstimator" /> supplies the size term separately. Note the rank and the tier
///         deliberately diverge for the IQ4 family: IQ4_NL/IQ4_XS rank near Q4 on quality but are graded the conservative
///         <see cref="GgufQuantTier.Small" /> for the picker.
///     </para>
/// </summary>
/// <remarks>Pure and stateless. Rank 0 is the best quality; larger ranks are progressively more compressed.</remarks>
public static class QuantLadder
{
    /// <summary>
    ///     The lowest quant the advisor will auto-recommend. Below this the model is dropped rather than offered at a
    ///     quality that degrades chat/coding output (the locked product floor).
    /// </summary>
    public const string DefaultFloorQuant = "Q3_K_M";

    // Best → worst. Each rung carries the curated fine quality order (the array index) AND the coarse tier the download
    // picker badges. Unknown / off-ladder labels are treated as just below Q4_K_M for ranking (see QualityRank); the
    // picker's GgufQuantQuality falls back to its own family rules for off-ladder tokens (the _L variants, legacy/ARM,…).
    private static readonly (string Quant, GgufQuantTier Tier)[] Rungs =
    [
        ("F32", GgufQuantTier.NearLossless),
        ("F16", GgufQuantTier.NearLossless),
        ("Q8_0", GgufQuantTier.NearLossless),
        ("Q6_K", GgufQuantTier.NearLossless),
        ("Q5_K_M", GgufQuantTier.SweetSpot),
        ("Q5_K_S", GgufQuantTier.SweetSpot),
        ("Q4_K_M", GgufQuantTier.Balanced),
        ("IQ4_NL", GgufQuantTier.Small),
        ("Q4_K_S", GgufQuantTier.Balanced),
        ("IQ4_XS", GgufQuantTier.Small),
        ("Q3_K_L", GgufQuantTier.Small),
        ("Q3_K_M", GgufQuantTier.Small),
        ("IQ3_M", GgufQuantTier.Small),
        ("IQ3_S", GgufQuantTier.Small),
        ("Q3_K_S", GgufQuantTier.Small),
        ("IQ3_XS", GgufQuantTier.Small),
        ("IQ3_XXS", GgufQuantTier.Small),
        ("Q2_K", GgufQuantTier.Minimal),
        ("IQ2_M", GgufQuantTier.Minimal),
        ("IQ2_S", GgufQuantTier.Minimal),
        ("IQ2_XS", GgufQuantTier.Minimal),
        ("IQ2_XXS", GgufQuantTier.Minimal),
        ("IQ1_M", GgufQuantTier.Minimal),
        ("IQ1_S", GgufQuantTier.Minimal)
    ];

    private static readonly Dictionary<string, int> RankByQuant =
        Rungs
            .Select(static (rung, index) => (rung.Quant, index))
            .ToDictionary(static pair => pair.Quant, static pair => pair.index, StringComparer.OrdinalIgnoreCase);

    // Rank assigned to an unknown label: immediately after Q4_K_M, matching the estimator's 4.5bpw fallback density.
    private static readonly int UnknownRank = Array.FindIndex(Rungs, static rung => rung.Quant == "Q4_K_M") + 1;

    /// <summary>The rank of the quality floor (<see cref="DefaultFloorQuant" />); quants ranked above this are off-limits.</summary>
    public static int FloorRank => RankByQuant[DefaultFloorQuant];

    /// <summary>
    ///     The quality rank of <paramref name="quant" /> — 0 is the best quality, larger is more compressed. An Unsloth
    ///     Dynamic (<c>UD-</c>) token is priced off its stripped base; an unknown or off-ladder label ranks just below
    ///     <c>Q4_K_M</c> (the estimator's conservative default density).
    /// </summary>
    public static int QualityRank(string quant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quant);
        return RankByQuant.TryGetValue(Normalize(quant), out var rank) ? rank : UnknownRank;
    }

    /// <summary><see langword="true" /> when <paramref name="quant" /> is at or above the quality floor (auto-recommendable).</summary>
    public static bool MeetsFloor(string quant)
    {
        return QualityRank(quant) <= FloorRank;
    }

    /// <summary>
    ///     <see langword="true" /> when <paramref name="quant" /> is a native, non-requantizable GGUF format. Today that is
    ///     MXFP4 (gpt-oss ships its MoE weights natively at ~4.25 bits/weight): re-quantizing such a model UP to a higher
    ///     nominal quant (Q6/Q8/…) only wastes space without adding quality — the weights are already at their trained
    ///     precision — so the advisor must never prefer a higher-quality requant over the native file. The advisor uses
    ///     this to cap the recommendable quality of a repo that ships a native format at the native file itself.
    /// </summary>
    public static bool IsNativeFormat(string quant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quant);
        return string.Equals(Normalize(quant), "MXFP4", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The coarse <see cref="GgufQuantTier" /> of <paramref name="quant" /> when it is a known rung (Unsloth Dynamic
    ///     tokens priced off their stripped base), or <see langword="null" /> when the token is off-ladder so the caller
    ///     (<see cref="GgufQuantQuality" />) applies its own family rules.
    /// </summary>
    public static GgufQuantTier? TierOf(string quant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quant);
        return RankByQuant.TryGetValue(Normalize(quant), out var rank) ? Rungs[rank].Tier : null;
    }

    // Trim + strip the Unsloth Dynamic (UD-) marker so a dynamic quant is priced off its base; the lookup dictionary is
    // case-insensitive so no upper-casing is needed here.
    private static string Normalize(string quant)
    {
        return GgufQuantParser.StripDynamicPrefix(quant.Trim());
    }
}
