namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

/// <summary>The constants the quant-fidelity axis is measured under.</summary>
/// <remarks>
///     They are constants rather than settings because a perplexity number is only comparable to another one measured
///     the same way, and the ones that CAN move (<see cref="DefaultChunks" />) are inside the KLD comparability digest
///     so a change is visible rather than silent.
/// </remarks>
public static class BenchmarkFidelityPolicy
{
    /// <summary>The perplexity window, pinned.</summary>
    /// <remarks>
    ///     Perplexity is only comparable at a fixed window, and every published llama.cpp / Unsloth / bartowski number
    ///     uses 512. The run's frozen placement, KV-cache type and flash-attn setting ARE replayed — those are what
    ///     differ between the runs being compared; the window is not.
    /// </remarks>
    public const int ContextTokens = 512;

    /// <summary>~102k tokens of prompt evaluation: about a minute on a 27B Q4_K_M, and enough to separate two quants.</summary>
    public const int DefaultChunks = 200;

    public const int MinimumChunks = 50;

    /// <summary>The whole wikitext-2-raw test split at a 512 window.</summary>
    public const int MaximumChunks = 655;

    /// <summary>
    ///     Bumped when the meaning of a stored KLD number changes for a reason no operator setting captures — a
    ///     llama.cpp logit-file format change, or a change to how this code drives the two phases.
    /// </summary>
    /// <remarks>
    ///     It is inside the comparability digest, so a bump renders every previously measured figure stale rather than
    ///     comparing it against numbers it no longer means the same thing as.
    /// </remarks>
    public const int KldFormatVersion = 1;

    /// <summary>Bytes per logit in llama.cpp's KL-divergence base file. MEASURED, not derived from the format.</summary>
    /// <remarks>
    ///     A real 10-chunk base file for Qwen3.8-27B (n_vocab 151 936) on an RTX 5090 came to 1 266 472 900 bytes over
    ///     777 912 320 logits, i.e. <b>1.628</b> — llama.cpp does not store a bare f16 per logit, so the format's 2.0
    ///     would have promised an operator 31.1 GB where the file is 25.3 GB. The constant carries ~7 % headroom over
    ///     the measurement because it is an ESTIMATE shown before a multi-gigabyte write, and the free-space
    ///     reservation below is what actually stops the write.
    /// </remarks>
    public const double KldBytesPerLogit = 1.75;

    /// <summary>The fixed part of the base file — its header and per-chunk bookkeeping.</summary>
    public const long KldHeaderBytes = 1024;

    /// <summary>
    ///     Free space that must remain AFTER the base write. A multi-gigabyte write that fills the disk to 100% is a
    ///     worse outcome than a refusal an operator can act on.
    /// </summary>
    public const long KldFreeSpaceHeadroomBytes = 10L * 1024 * 1024 * 1024;

    /// <summary>The vocabulary the disk estimate assumes.</summary>
    /// <remarks>
    ///     The registry does not record a model's <c>n_vocab</c>, and the estimate exists to REFUSE a write that will
    ///     not fit, so it assumes the largest vocabulary among the families this app runs (Gemma-3's 262 144) rather
    ///     than a typical one: an over-estimate costs an operator a refusal they can override by freeing space, an
    ///     under-estimate costs them a full disk. simplified: a fixed ceiling instead of reading n_vocab out of the GGUF
    ///     header — read the header if the over-estimate ever refuses a write that would in fact have fit.
    /// </remarks>
    public const int DefaultVocabSize = 262_144;

    public static int ClampChunks(int? chunks) =>
        chunks is not { } value ? DefaultChunks : Math.Clamp(value, MinimumChunks, MaximumChunks);

    /// <summary>An upper bound on the base-logit file for a model of this vocabulary at this chunk count.</summary>
    public static long EstimateKldBytes(int chunks, int vocabSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(chunks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(vocabSize);
        return KldHeaderBytes + (long)Math.Ceiling(chunks * (double)ContextTokens * vocabSize * KldBytesPerLogit);
    }
}
