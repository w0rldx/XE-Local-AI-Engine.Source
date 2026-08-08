namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

public enum LlamaStartupFailureKind
{
    Other = 0,
    OutOfMemory = 1,
    KvOrFlashAttentionIncompatible = 2
}

/// <summary>Classifies bounded startup diagnostics without exposing them beyond the supervisor.</summary>
/// <remarks>
///     <para>
///         Classification is PER LINE and then reduced, never a substring test over the joined capture. Joining first
///         lets any one line's vocabulary decide the whole diagnosis: llama.cpp raises
///         <c>failed to allocate buffer for kv cache</c> on a genuine KV-cache allocation failure, so a joined buffer
///         containing both that line and a <c>cudaMalloc failed: out of memory</c> line matched the compatibility
///         branch and hid the out-of-memory verdict the supervisor's context down-tier is gated on.
///     </para>
///     <para>
///         Two precedence rules, both deliberate. WITHIN a line, an unambiguous out-of-memory phrase wins, then a
///         KV/flash-attention compatibility marker, then generic allocation-failure wording — so a line reporting an
///         unsupported cache type stays a compatibility verdict even though it also says it failed to allocate.
///         ACROSS lines, out-of-memory outranks compatibility: an allocation failure is hardware evidence that stands
///         on its own, while another line merely naming the KV cache is not evidence against it.
///     </para>
///     <para>
///         The compatibility markers name a cache TYPE or flash attention, never the KV cache as a component. That is
///         the distinction the joined buffer erased: failing to allocate the KV cache is an allocation failure, and
///         only a rejected cache type or a flash-attention requirement is a compatibility problem.
///     </para>
/// </remarks>
public static class LlamaStartupFailureClassifier
{
    /// <summary>Phrases that state a memory allocation failed outright, whatever else the line mentions.</summary>
    private static readonly string[] OutOfMemoryMarkers = ["out of memory", "cudamalloc failed", "cuda error 2"];

    /// <summary>
    ///     Phrases that reject a KV cache type or demand flash attention, e.g. llama.cpp's
    ///     <c>V cache quantization requires flash_attn</c> and <c>K cache type ... does not divide n_embd_head_k</c>.
    /// </summary>
    private static readonly string[] IncompatibilityMarkers =
        ["flash attention", "flash_attn", "cache type", "cache quantization", "-ctk", "-ctv"];

    /// <summary>Weaker allocation wording, checked only after the compatibility markers have had their say.</summary>
    private static readonly string[] AllocationFailureMarkers = ["failed to allocate"];

    public static LlamaStartupFailureKind Classify(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var verdict = LlamaStartupFailureKind.Other;
        foreach (var line in lines)
        {
            switch (ClassifyLine(line))
            {
                case LlamaStartupFailureKind.OutOfMemory:
                    return LlamaStartupFailureKind.OutOfMemory;
                case LlamaStartupFailureKind.KvOrFlashAttentionIncompatible:
                    verdict = LlamaStartupFailureKind.KvOrFlashAttentionIncompatible;
                    break;
                case LlamaStartupFailureKind.Other:
                default:
                    break;
            }
        }

        return verdict;
    }

    private static LlamaStartupFailureKind ClassifyLine(string line)
    {
        if (ContainsAny(line, OutOfMemoryMarkers))
        {
            return LlamaStartupFailureKind.OutOfMemory;
        }

        if (ContainsAny(line, IncompatibilityMarkers))
        {
            return LlamaStartupFailureKind.KvOrFlashAttentionIncompatible;
        }

        return ContainsAny(line, AllocationFailureMarkers)
            ? LlamaStartupFailureKind.OutOfMemory
            : LlamaStartupFailureKind.Other;
    }

    private static bool ContainsAny(string line, string[] markers)
    {
        return Array.Exists(markers, marker => line.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
