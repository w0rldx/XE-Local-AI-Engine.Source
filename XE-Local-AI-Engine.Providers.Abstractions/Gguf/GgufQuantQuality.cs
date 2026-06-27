namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Pure, hardware-free classifier mapping a GGUF quant token to a coarse <see cref="GgufQuantTier" /> quality grade.
///     Total: every input yields a tier and it never throws — an Unsloth Dynamic (<c>UD-</c>) token is priced off its
///     stripped base (via <see cref="GgufQuantParser.StripDynamicPrefix" />) and an unrecognized token defaults to
///     <see cref="GgufQuantTier.Balanced" /> (the safe middle) rather than the lowest grade.
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

        return token switch
        {
            // NearLossless — 6-/8-bit K-quants and float formats; visually/numerically near the source weights.
            "Q8_0" or "Q6_K" or "Q6_K_L" or "F16" or "FP16" or "BF16" or "F32" or "FP32" or "F64" => GgufQuantTier.NearLossless,
            // SweetSpot — 5-bit K-quants: best quality-per-byte for most users.
            "Q5_K_M" or "Q5_K_S" or "Q5_K_L" => GgufQuantTier.SweetSpot,
            // Balanced — 4-bit K-quants: the common default.
            "Q4_K_M" or "Q4_K_S" or "Q4_K_L" => GgufQuantTier.Balanced,
            // Minimal — 2-bit K-quant.
            "Q2_K" => GgufQuantTier.Minimal,
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
