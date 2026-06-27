namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Coarse quality grade for a GGUF quant token, independent of any hardware. Used by the download picker to hint how
///     close a quant is to the source weights. The underlying integer is an ORDERED rank (higher = better quality), so
///     tiers compare directly (<c>GgufQuantTier.SweetSpot &gt; GgufQuantTier.Small</c>) for "pick the best tier" logic.
/// </summary>
public enum GgufQuantTier
{
    /// <summary>Strongest compression, largest quality loss: 2-bit K-quants and 1-/2-bit IQ (Q2_K, IQ1_*, IQ2_*).</summary>
    Minimal = 0,

    /// <summary>Compact but lossy: 3-bit K-quants, IQ3_*/IQ4_XS/IQ4_NL, and legacy/ARM 4-/5-bit (Q4_0, Q5_0, …).</summary>
    Small = 1,

    /// <summary>The common 4-bit K-quant default — a reasonable quality/size balance (Q4_K_S/M/L).</summary>
    Balanced = 2,

    /// <summary>Best quality-per-byte for most users — 5-bit K-quants (Q5_K_S/M/L).</summary>
    SweetSpot = 3,

    /// <summary>Near the source weights: 6-bit/8-bit K-quants and float formats (Q6_K, Q8_0, F16, BF16, F32, …).</summary>
    NearLossless = 4
}
