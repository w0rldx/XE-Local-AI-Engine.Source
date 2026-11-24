namespace XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>Values the GGUF header declares, as written by the converter; each is null when the key is absent.</summary>
/// <remarks>A number outside 0..2^53-1 is null: JSON clients read numbers as doubles, so a larger one would arrive changed.</remarks>
public sealed record ModelLabArchitectureHyperparameters
{
    /// <summary>The <c>general.file_type</c> label (<c>Q4_K_M</c>), or its raw number when unnamed.</summary>
    public required string? FileType { get; init; }

    /// <summary><c>general.parameter_count</c>; never the summed tensor sizes.</summary>
    public required long? DeclaredParamCount { get; init; }

    /// <summary><c>{arch}.block_count</c>, including the MTP blocks.</summary>
    public required long? BlockCount { get; init; }

    /// <summary><c>{arch}.nextn_predict_layers</c>: the MTP blocks counted in <see cref="BlockCount" />.</summary>
    public required long? NextnPredictLayers { get; init; }

    /// <summary><c>split.count</c> of a sharded model.</summary>
    public required long? SplitCount { get; init; }

    public required long? AttentionHeadCount { get; init; }

    public required long? AttentionHeadCountKv { get; init; }

    public required long? AttentionKeyLength { get; init; }

    public required long? AttentionValueLength { get; init; }

    public required long? EmbeddingLength { get; init; }

    public required long? ContextLength { get; init; }

    public required long? ExpertCount { get; init; }

    public required long? ExpertUsedCount { get; init; }

    /// <summary><c>{arch}.full_attention_interval</c>: every Nth block is full attention.</summary>
    public required long? FullAttentionInterval { get; init; }
}
