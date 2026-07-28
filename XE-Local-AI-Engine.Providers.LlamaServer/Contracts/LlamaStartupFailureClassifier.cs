namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

public enum LlamaStartupFailureKind
{
    Other = 0,
    OutOfMemory = 1,
    KvOrFlashAttentionIncompatible = 2
}

/// <summary>Classifies bounded startup diagnostics without exposing them beyond the supervisor.</summary>
public static class LlamaStartupFailureClassifier
{
    public static LlamaStartupFailureKind Classify(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var text = string.Join('\n', lines);
        if (text.Contains("flash attention", StringComparison.OrdinalIgnoreCase)
            || text.Contains("kv cache", StringComparison.OrdinalIgnoreCase)
            || text.Contains("-ctk", StringComparison.OrdinalIgnoreCase)
            || text.Contains("-ctv", StringComparison.OrdinalIgnoreCase))
        {
            return LlamaStartupFailureKind.KvOrFlashAttentionIncompatible;
        }

        return text.Contains("out of memory", StringComparison.OrdinalIgnoreCase)
               || text.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase)
               || text.Contains("cuda error 2", StringComparison.OrdinalIgnoreCase)
            ? LlamaStartupFailureKind.OutOfMemory
            : LlamaStartupFailureKind.Other;
    }
}
