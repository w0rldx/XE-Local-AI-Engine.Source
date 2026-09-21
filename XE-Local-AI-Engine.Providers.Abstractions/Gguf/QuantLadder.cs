namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

using System.Collections.ObjectModel;

/// <summary>
///     The single source of truth for GGUF quant quality: a llama.cpp quant ladder ordered by quality (best → worst),
///     where each known token carries BOTH a fine-grained quality RANK and the coarse <see cref="GgufQuantTier" /> grade.
/// </summary>
/// <remarks>
///     Pure and stateless: rank 0 is the best quality, larger ranks are progressively more compressed. Two consumers read one table, so
///     the quant knowledge is defined once: the advisor walks the fine <see cref="QualityRank" /> and <see cref="DefaultFloorQuant" /> to
///     step down to the highest quant that fits; the picker's <see cref="GgufQuantQuality" /> reads <see cref="TierOf" /> for the coarse
///     badge. Quality is NOT a strict function of bytes-per-weight across families (an I-quant beats a same-bit K-quant, a native FP4
///     beats a wider requant), so the order is the curated quality ranking and <see cref="MemoryFitEstimator" /> supplies the size term.
/// </remarks>
public static class QuantLadder
{
    /// <summary>
    ///     The lowest quant the advisor will auto-recommend. Below this the model is dropped rather than offered at a
    ///     quality that degrades chat/coding output (the locked product floor).
    /// </summary>
    public const string DefaultFloorQuant = "Q3_K_M";

    // Best → worst. Each rung carries the curated fine quality order (the array index) AND the coarse tier the download picker badges.
    // Unknown / off-ladder labels rank just below Q4_K_M (see QualityRank); GgufQuantQuality applies its own family rules to them.
    private static readonly QuantRung[] Rungs =
    [
        new() { Quant = "F32", Tier = GgufQuantTier.NearLossless },
        new() { Quant = "F16", Tier = GgufQuantTier.NearLossless },
        new() { Quant = "Q8_0", Tier = GgufQuantTier.NearLossless },
        new() { Quant = "Q6_K", Tier = GgufQuantTier.NearLossless },
        new() { Quant = "Q5_K_M", Tier = GgufQuantTier.SweetSpot },
        new() { Quant = "Q5_K_S", Tier = GgufQuantTier.SweetSpot },
        // NVFP4 leads MXFP4: finer scale granularity (a 16-element block with an FP8 scale vs MXFP4's 32-element block with a power-of-two scale) at the same measured on-disk density.
        // Off the ladder both take UnknownRank — one step past the "recommended" gate, demoting a native-FP4 repo to "Can run" however well it fits. See IsNativeFormat.
        new() { Quant = "NVFP4", Tier = GgufQuantTier.Balanced },
        new() { Quant = "MXFP4", Tier = GgufQuantTier.Balanced },
        new() { Quant = "Q4_K_M", Tier = GgufQuantTier.Balanced },
        new() { Quant = "IQ4_NL", Tier = GgufQuantTier.Small },
        new() { Quant = "Q4_K_S", Tier = GgufQuantTier.Balanced },
        new() { Quant = "IQ4_XS", Tier = GgufQuantTier.Small },
        new() { Quant = "Q3_K_L", Tier = GgufQuantTier.Small },
        new() { Quant = "Q3_K_M", Tier = GgufQuantTier.Small },
        new() { Quant = "IQ3_M", Tier = GgufQuantTier.Small },
        new() { Quant = "IQ3_S", Tier = GgufQuantTier.Small },
        new() { Quant = "Q3_K_S", Tier = GgufQuantTier.Small },
        new() { Quant = "IQ3_XS", Tier = GgufQuantTier.Small },
        new() { Quant = "IQ3_XXS", Tier = GgufQuantTier.Small },
        new() { Quant = "Q2_K", Tier = GgufQuantTier.Minimal },
        new() { Quant = "IQ2_M", Tier = GgufQuantTier.Minimal },
        new() { Quant = "IQ2_S", Tier = GgufQuantTier.Minimal },
        new() { Quant = "IQ2_XS", Tier = GgufQuantTier.Minimal },
        new() { Quant = "IQ2_XXS", Tier = GgufQuantTier.Minimal },
        new() { Quant = "IQ1_M", Tier = GgufQuantTier.Minimal },
        new() { Quant = "IQ1_S", Tier = GgufQuantTier.Minimal }
    ];

    private static readonly ReadOnlyCollection<string> CanonicalQuantizationValues =
        Array.AsReadOnly(Rungs.Select(static rung => rung.Quant).ToArray());

    private static readonly Dictionary<string, int> RankByQuant =
        Rungs
            .Select(static (rung, index) => (rung.Quant, index))
            .ToDictionary(static pair => pair.Quant, static pair => pair.index, StringComparer.OrdinalIgnoreCase);

    // Rank assigned to an unknown label: immediately after Q4_K_M, matching the estimator's 4.5bpw fallback density.
    private static readonly int UnknownRank = Array.FindIndex(Rungs, static rung => rung.Quant == "Q4_K_M") + 1;

    /// <summary>The rank of the quality floor (<see cref="DefaultFloorQuant" />); quants ranked above this are off-limits.</summary>
    public static int FloorRank => RankByQuant[DefaultFloorQuant];

    /// <summary>
    ///     Repository-owned canonical quantizations that may be selected when a GGUF header and filename cannot
    ///     identify the source quantization.
    /// </summary>
    /// <remarks>
    ///     The returned collection is immutable and ordered best-to-worst by the same quality ladder used by model-fit
    ///     selection.
    /// </remarks>
    public static IReadOnlyList<string> CanonicalQuantizations => CanonicalQuantizationValues;

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
    ///     <see langword="true" /> when <paramref name="quant" /> is a native, non-requantizable GGUF format — today
    ///     MXFP4 and NVFP4.
    /// </summary>
    /// <remarks>
    ///     gpt-oss ships its MoE weights natively at MXFP4's ~4.25 bits/weight; NVFP4 is NVIDIA's Blackwell-era FP4, which llama.cpp
    ///     carries as <c>GGML_TYPE_NVFP4</c> with sm_120-tuned kernels. Both therefore rank ABOVE Q4_K_M on the ladder despite sizing
    ///     narrower (4.25 vs 4.5 bits/weight) — the same rank-vs-bytes divergence the IQ4 family shows. Re-quantizing such a model UP to
    ///     a higher nominal quant (Q6/Q8/…) only wastes space without adding quality, the weights already being at their trained
    ///     precision, so the advisor never prefers a requant over the native file and caps a native repo's quality at that file.
    /// </remarks>
    public static bool IsNativeFormat(string quant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quant);
        var normalized = Normalize(quant);
        return string.Equals(normalized, "MXFP4", StringComparison.OrdinalIgnoreCase)
               || string.Equals(normalized, "NVFP4", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The coarse <see cref="GgufQuantTier" /> of <paramref name="quant" /> when it is a known rung (Unsloth Dynamic
    ///     tokens priced off their stripped base), or <see langword="null" /> when the token is off-ladder so the caller
    ///     (<see cref="GgufQuantQuality" />) applies its own family rules.
    /// </summary>
    /// <remarks>
    ///     Rank and tier deliberately diverge for the IQ4 family: IQ4_NL/IQ4_XS rank near Q4 on quality but are graded
    ///     the conservative <see cref="GgufQuantTier.Small" /> for the picker.
    /// </remarks>
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

    /// <summary>One rung of the ladder: the canonical quant token and the coarse tier the download picker badges.</summary>
    private sealed record QuantRung
    {
        public required string Quant { get; init; }

        public required GgufQuantTier Tier { get; init; }
    }
}
