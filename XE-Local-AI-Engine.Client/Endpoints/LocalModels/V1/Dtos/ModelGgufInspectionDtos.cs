namespace XE_Local_AI_Engine.Client.Endpoints.LocalModels.V1;

/// <summary>Request DTO for inspecting an installed GGUF. <see cref="ModelName" /> is bound from the route.</summary>
public sealed class GetModelGgufInspectionRequest
{
    public string? ModelName { get; init; }
}

/// <summary>An installed GGUF's header metadata and its tensors grouped per transformer block.</summary>
public sealed class ModelGgufInspectionResponse
{
    public required string ModelName { get; init; }

    /// <summary>Why the read stopped early (<c>unreadable</c>, <c>unsupportedVersion</c>, <c>truncated</c>, <c>malformed</c>), or null when it completed.</summary>
    public required string? ReadFailure { get; init; }

    public required int? GgufVersion { get; init; }

    public required string? Architecture { get; init; }

    public required string? DisplayName { get; init; }

    /// <summary>The exact parameter count: <c>general.parameter_count</c>, else the summed tensor element count; null when neither is known.</summary>
    public required long? ParameterCount { get; init; }

    public required string? FileType { get; init; }

    public required long? BlockCount { get; init; }

    public required long? AttentionHeadCount { get; init; }

    public required long? AttentionHeadCountKv { get; init; }

    public required long? EmbeddingLength { get; init; }

    public required long? ContextLength { get; init; }

    public required long? ExpertCount { get; init; }

    public required string? ChatTemplate { get; init; }

    /// <summary>The shard count of a split model; only the first shard's tensors are listed.</summary>
    public required long? SplitCount { get; init; }

    public required long TensorCount { get; init; }

    /// <summary>Sum of every listed tensor's stored size, or null when any tensor's size is unknown.</summary>
    public required long? TotalTensorBytes { get; init; }

    public required long HeaderBytes { get; init; }

    public required long FileSizeBytes { get; init; }

    /// <summary>The <c>blk.&lt;n&gt;.*</c> tensors, one group per block index in ascending order.</summary>
    public required IReadOnlyList<ModelGgufTensorBlockDto> Blocks { get; init; }

    /// <summary>Tensors outside any block (<c>token_embd</c>, <c>output_norm</c>, <c>output</c>), in file order.</summary>
    public required IReadOnlyList<ModelGgufTensorDto> NonBlockTensors { get; init; }
}

/// <summary>The tensors of one transformer block.</summary>
public sealed class ModelGgufTensorBlockDto
{
    public required int Index { get; init; }

    /// <summary>Sum of the block's tensor sizes, or null when any size is unknown.</summary>
    public required long? ByteSize { get; init; }

    public required IReadOnlyList<ModelGgufTensorDto> Tensors { get; init; }
}

/// <summary>One tensor: full name, shape in GGUF order, ggml type and stored size.</summary>
public sealed class ModelGgufTensorDto
{
    public required string Name { get; init; }

    public required IReadOnlyList<long> Shape { get; init; }

    /// <summary>The ggml type name, or null for an id the node does not know (see <see cref="GgmlTypeId" />).</summary>
    public required string? GgmlType { get; init; }

    public required int GgmlTypeId { get; init; }

    public required long Offset { get; init; }

    public required long? ByteSize { get; init; }
}
