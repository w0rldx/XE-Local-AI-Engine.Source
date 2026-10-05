namespace XE_Local_AI_Engine.Providers.HuggingFace.Implementation;

/// <summary>
///     Standardized GGUF header metadata extracted via a range read. Any field absent from the header is
///     <see langword="null" />.
/// </summary>
internal sealed record GgufHeaderMetadata
{
    /// <summary>
    ///     True when the remote read stopped at the tokenizer with the architecture block set, before the key-value
    ///     section ended: keys written after that point were not read.
    /// </summary>
    public bool IsPartial { get; init; }

    public required string? Architecture { get; init; }

    public required string? QuantType { get; init; }

    public required long? ParamCount { get; init; }

    public required long? BlockCount { get; init; }

    public required long? AttentionHeadCount { get; init; }

    public required long? AttentionHeadCountKV { get; init; }

    public required long? EmbeddingLength { get; init; }

    public required long? ContextLength { get; init; }

    public required string? ChatTemplate { get; init; }

    public required long? ExpertCount { get; init; }

    public required long? ExpertUsedCount { get; init; }

    public long? AttentionKeyLength { get; init; }

    public long? AttentionValueLength { get; init; }

    public long? SlidingWindow { get; init; }

    public long? SlidingWindowPattern { get; init; }

    public long? AttentionKeyLengthMla { get; init; }

    public long? AttentionValueLengthMla { get; init; }

    public long? FullAttentionInterval { get; init; }

    public static GgufHeaderMetadata Empty { get; } = new()
    {
        Architecture = null,
        QuantType = null,
        ParamCount = null,
        BlockCount = null,
        AttentionHeadCount = null,
        AttentionHeadCountKV = null,
        EmbeddingLength = null,
        ContextLength = null,
        ChatTemplate = null,
        ExpertCount = null,
        ExpertUsedCount = null,
        AttentionKeyLength = null,
        AttentionValueLength = null,
        SlidingWindow = null,
        SlidingWindowPattern = null,
        AttentionKeyLengthMla = null,
        AttentionValueLengthMla = null,
        FullAttentionInterval = null
    };

    /// <summary>True when the GGUF declares a positive expert count — a Mixture-of-Experts model (dense models omit it).</summary>
    public bool IsMoe => ExpertCount is > 0;
}
