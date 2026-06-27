namespace XE_Local_AI_Engine.Client.Services.ModelFit.Fit;

/// <summary>
///     The llama.cpp quant ladder ordered by quality (best → worst by effective bits-per-weight). The advisor walks this
///     ladder to pick the highest-quality quant of a repo that still fits the memory budget instead of dropping the whole
///     repo when its default <c>Q4_K_M</c> file is too large. Quality is NOT a strict function of bytes-per-weight across
///     families (an I-quant beats a K-quant of the same bit count), so the order is the curated quality ranking from the
///     llama.cpp / 2026 GGUF guidance — <see cref="MemoryFitEstimator.BytesPerWeight" /> supplies the size term separately.
/// </summary>
/// <remarks>Pure and stateless. Rank 0 is the best quality; larger ranks are progressively more compressed.</remarks>
public static class QuantLadder
{
    /// <summary>
    ///     The lowest quant the advisor will auto-recommend. Below this the model is dropped rather than offered at a
    ///     quality that degrades chat/coding output (the locked product floor).
    /// </summary>
    public const string DefaultFloorQuant = "Q3_K_M";

    // Best → worst. Unknown / off-ladder labels are treated as just below Q4_K_M (see QualityRank) — the conservative
    // default density the estimator already uses — so they never crowd out a genuinely higher-quality known quant.
    private static readonly string[] DescendingByQuality =
    [
        "F32",
        "F16",
        "Q8_0",
        "Q6_K",
        "Q5_K_M",
        "Q5_K_S",
        "Q4_K_M",
        "IQ4_NL",
        "Q4_K_S",
        "IQ4_XS",
        "Q3_K_L",
        "Q3_K_M",
        "IQ3_M",
        "IQ3_S",
        "Q3_K_S",
        "IQ3_XS",
        "IQ3_XXS",
        "Q2_K",
        "IQ2_M",
        "IQ2_S",
        "IQ2_XS",
        "IQ2_XXS",
        "IQ1_M",
        "IQ1_S"
    ];

    private static readonly Dictionary<string, int> RankByQuant =
        DescendingByQuality
            .Select(static (quant, index) => (quant, index))
            .ToDictionary(static pair => pair.quant, static pair => pair.index, StringComparer.OrdinalIgnoreCase);

    // Rank assigned to an unknown label: immediately after Q4_K_M, matching the estimator's 4.5bpw fallback density.
    private static readonly int UnknownRank = Array.IndexOf(DescendingByQuality, "Q4_K_M") + 1;

    /// <summary>
    ///     The quality rank of <paramref name="quant" /> — 0 is the best quality, larger is more compressed. An unknown or
    ///     off-ladder label ranks just below <c>Q4_K_M</c> (the estimator's conservative default density).
    /// </summary>
    public static int QualityRank(string quant)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(quant);
        return RankByQuant.TryGetValue(quant.Trim(), out var rank) ? rank : UnknownRank;
    }

    /// <summary>The rank of the quality floor (<see cref="DefaultFloorQuant" />); quants ranked above this are off-limits.</summary>
    public static int FloorRank => RankByQuant[DefaultFloorQuant];

    /// <summary><see langword="true" /> when <paramref name="quant" /> is at or above the quality floor (auto-recommendable).</summary>
    public static bool MeetsFloor(string quant)
    {
        return QualityRank(quant) <= FloorRank;
    }
}
