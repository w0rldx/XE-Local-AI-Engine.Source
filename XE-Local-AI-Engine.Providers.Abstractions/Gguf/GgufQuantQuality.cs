namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Pure, hardware-free classifier mapping a GGUF quant token to a coarse <see cref="GgufQuantTier" /> quality grade.
///     Total: every input yields a tier and it never throws — an Unsloth Dynamic (<c>UD-</c>) token is priced off its
///     stripped base (via <see cref="GgufQuantParser.StripDynamicPrefix" />) and an unrecognized token defaults to
///     <see cref="GgufQuantTier.Balanced" /> (the safe middle) rather than the lowest grade.
///     <para>
///         The core quant tokens' tiers live in <see cref="QuantLadder" /> (the single source of truth it shares with the
///         advisor's fine quality rank); this classifier delegates to it and only adds the off-ladder aliases (the
///         <c>_L</c> variants and float aliases) plus the family fallback for tokens neither table enumerates.
///     </para>
/// </summary>
public static class GgufQuantQuality
{
    /// <summary>
    ///     Grades <paramref name="quant" /> (a canonical or raw quant token, optionally <c>UD-</c>-prefixed). Returns
    ///     <see cref="GgufQuantTier.Balanced" /> for a null/blank/unrecognized token so the caller never has to guard.
    /// </summary>
    public static GgufQuantTier Classify(string? quant)
    {
        if (string.IsNullOrWhiteSpace(quant))
        {
            return GgufQuantTier.Balanced;
        }

        var token = GgufQuantParser.StripDynamicPrefix(quant.Trim()).ToUpperInvariant();

        // The quant ladder owns the core tokens' tier (and their fine quality rank). Off-ladder tokens fall through.
        if (QuantLadder.TierOf(token) is { } ladderTier)
        {
            return ladderTier;
        }

        return token switch
        {
            // NearLossless aliases the ladder does not enumerate: the _L 6-bit K-quant and the extra float formats.
            "Q6_K_L" or "FP16" or "BF16" or "FP32" or "F64" => GgufQuantTier.NearLossless,
            // SweetSpot / Balanced _L variants.
            "Q5_K_L" => GgufQuantTier.SweetSpot,
            "Q4_K_L" => GgufQuantTier.Balanced,
            _ => ClassifyByFamily(token)
        };
    }

    private static GgufQuantTier ClassifyByFamily(string token)
    {
        // ARM-packed legacy 4-bit (Q4_0_4_4 / Q4_0_4_8 / Q4_0_8_8) and the plain legacy 4-/5-bit quants → Small.
        if (token.StartsWith("Q4_0", StringComparison.Ordinal)
            || token is "Q4_1" or "Q5_0" or "Q5_1")
        {
            return GgufQuantTier.Small;
        }

        // 3-bit K-quants (Q3_K_S/M/L/XL) → Small.
        if (token.StartsWith("Q3_K", StringComparison.Ordinal))
        {
            return GgufQuantTier.Small;
        }

        // IQ3_* and IQ4_* (IQ4_XS, IQ4_NL) → Small.
        if (token.StartsWith("IQ3", StringComparison.Ordinal)
            || token.StartsWith("IQ4", StringComparison.Ordinal))
        {
            return GgufQuantTier.Small;
        }

        // 1-/2-bit IQ (IQ1_*, IQ2_*) → Minimal.
        if (token.StartsWith("IQ1", StringComparison.Ordinal)
            || token.StartsWith("IQ2", StringComparison.Ordinal))
        {
            return GgufQuantTier.Minimal;
        }

        // Unrecognized token → Balanced (safe middle; never throw).
        return GgufQuantTier.Balanced;
    }
}
