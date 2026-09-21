namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

public enum LlamaStartupFailureKind
{
    Other = 0,
    OutOfMemory = 1,
    KvOrFlashAttentionIncompatible = 2
}

/// <summary>Classifies bounded startup diagnostics without exposing them beyond the supervisor.</summary>
/// <remarks>
///     Classification is PER LINE and then reduced, NEVER a substring test over the joined capture, which would let any
///     one line's vocabulary decide the whole diagnosis. Two precedence rules: WITHIN a line an unambiguous
///     out-of-memory phrase wins, then a KV or flash-attention compatibility marker, then generic allocation-failure
///     wording; ACROSS lines out-of-memory outranks compatibility. The markers name a cache TYPE or flash attention,
///     never the KV cache as a component. See docs/wiki/03-local-runtime-and-providers.md, "Startup failure classification".
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
