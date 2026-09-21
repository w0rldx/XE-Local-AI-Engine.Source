namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>
///     Per-architecture attention defaults that llama.cpp hardcodes but a GGUF header does not always carry — currently
///     the interleaved sliding-window-attention (SWA) layer stride.
/// </summary>
/// <remarks>
///     The stride is how many layers apart the full-attention ("global") layers sit, every layer in between being a
///     window-limited ("local") layer: Gemma3 uses a 5:1 local:global pattern (a global layer every 6th layer), Gemma2
///     alternates 1:1 (a global layer every 2nd). The GGUF header reader resolves the SWA pattern from it when the header
///     omits an explicit <c>{arch}.attention.sliding_window_pattern</c> key, so the memory-fit estimator can size the KV
///     cache with the window-limited layers capped at the window rather than the full context.
/// </remarks>
public static class GgufAttentionDefaults
{
    /// <summary>
    ///     The global-attention stride for <paramref name="architecture" /> — every Nth layer is full attention, the rest
    ///     window-limited — or <see langword="null" /> when the architecture is not a known interleaved-SWA family.
    /// </summary>
    /// <remarks>
    ///     A null result tells the estimator to keep every layer full-attention (a deliberately conservative
    ///     over-estimate), never to guess a reduction for an architecture whose layer pattern is unknown.
    /// </remarks>
    public static long? SlidingWindowPattern(string? architecture)
    {
        if (string.IsNullOrWhiteSpace(architecture))
        {
            return null;
        }

        return architecture.Trim().ToUpperInvariant() switch
        {
            "GEMMA3" or "GEMMA3TEXT" or "GEMMA3_TEXT" => 6L,
            "GEMMA2" => 2L,
            _ => null
        };
    }
}
