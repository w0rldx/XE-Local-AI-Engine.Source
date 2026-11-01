namespace XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>Why a GGUF tensor-block read stopped before it listed every tensor; <see cref="None" /> means it completed.</summary>
public enum GgufTensorReadFailure
{
    None = 0,

    /// <summary>The file is missing, unreadable or not a GGUF file.</summary>
    Unreadable,

    /// <summary>A GGUF version this reader does not parse (only v2 and v3 are read).</summary>
    UnsupportedVersion,

    /// <summary>The file ends inside the header, the key-value section or the tensor-info block.</summary>
    Truncated,

    /// <summary>A value breaks the format or a parser bound (dimension count, tensor count, name length, size overflow).</summary>
    Malformed
}

/// <summary>One tensor-info record of a GGUF file: its name, shape, ggml storage type and data-section offset.</summary>
public sealed record GgufTensorInfo
{
    public required string Name { get; init; }

    /// <summary>The tensor's dimensions in GGUF order (innermost first), at most eight.</summary>
    public required IReadOnlyList<long> Dimensions { get; init; }

    /// <summary>The raw <c>ggml_type</c> id.</summary>
    public required uint GgmlType { get; init; }

    /// <summary>The <c>ggml_type</c> name (<c>Q4_K</c>), or <see langword="null" /> for an id this reader does not know.</summary>
    public required string? GgmlTypeName { get; init; }

    /// <summary>Offset of the tensor's data relative to the start of the data section.</summary>
    public required long Offset { get; init; }

    /// <summary>The product of <see cref="Dimensions" />: the tensor's parameter count.</summary>
    public required long ElementCount { get; init; }

    /// <summary>Stored size in bytes, or <see langword="null" /> when the type is unknown or the shape does not fill whole blocks.</summary>
    public required long? ByteSize { get; init; }
}

/// <summary>The header metadata and tensor list of one local GGUF file, read without touching the tensor data.</summary>
public sealed class GgufTensorInspection
{
    public required GgufTensorReadFailure Failure { get; init; }

    public required uint? Version { get; init; }

    public required string? Architecture { get; init; }

    /// <summary>The converter's <c>general.name</c>, or <see langword="null" /> when absent.</summary>
    public required string? ModelName { get; init; }

    /// <summary><c>general.parameter_count</c>, else the summed tensor element count of a complete read, else <see langword="null" />.</summary>
    public required long? ParamCount { get; init; }

    /// <summary>The <c>general.file_type</c> label (<c>Q4_K_M</c>), its raw number when unnamed, or <see langword="null" /> when absent.</summary>
    public required string? FileType { get; init; }

    public required long? BlockCount { get; init; }

    public required long? AttentionHeadCount { get; init; }

    public required long? AttentionHeadCountKv { get; init; }

    public required long? EmbeddingLength { get; init; }

    public required long? ContextLength { get; init; }

    public required long? ExpertCount { get; init; }

    public required string? ChatTemplate { get; init; }

    /// <summary>The <c>split.count</c> of a sharded model (this file lists only its own shard's tensors), or <see langword="null" />.</summary>
    public required long? SplitCount { get; init; }

    /// <summary>The declared tensor count; <see cref="Tensors" /> holds fewer only when <see cref="Failure" /> is set.</summary>
    public required long TensorCount { get; init; }

    public required IReadOnlyList<GgufTensorInfo> Tensors { get; init; }

    /// <summary>Bytes from the start of the file to the end of the tensor-info block (0 when the read failed).</summary>
    public required long HeaderBytes { get; init; }

    public required long FileSizeBytes { get; init; }
}
