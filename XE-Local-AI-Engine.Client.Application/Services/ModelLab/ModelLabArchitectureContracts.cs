namespace XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>An installed GGUF's block layout and tensors keyed by component, read from the header alone.</summary>
/// <remarks>
///     Groups are read from the file; templates are inferred from llama.cpp and carry their source. A component is
///     <c>blk.{index}.{component}</c>, or <c>embedding</c>, <c>output_norm</c>, <c>output</c>, <c>other</c> outside the blocks.
/// </remarks>
public sealed record ModelLabArchitectureResponse
{
    public required int DescriptorVersion { get; init; }

    public required string ModelName { get; init; }

    public required string? Architecture { get; init; }

    /// <summary><c>detailed</c> (HF names and templates), <c>overview</c> (blocks and groups only) or <c>unreadable</c>.</summary>
    public required string Support { get; init; }

    /// <summary>Why the GGUF read stopped (<c>unreadable</c>, <c>unsupportedVersion</c>, <c>truncated</c>, <c>malformed</c>), or null.</summary>
    public required string? ReadFailure { get; init; }

    /// <summary>The <c>owner/name</c> of the header's source repo, else its first base model, when that URL is exactly <c>https://huggingface.co/owner/name</c>.</summary>
    /// <remarks>A prefill for the config-only fetch, never trusted as a pairing.</remarks>
    public required string? SuggestedBaseRepo { get; init; }

    /// <summary>One entry per <c>blk.N</c> index in ascending order; empty when the file was unreadable.</summary>
    public required IReadOnlyList<ModelLabArchitectureBlock> Blocks { get; init; }

    /// <summary>One operation graph per block kind present; empty unless <see cref="Support" /> is <c>detailed</c>.</summary>
    public required IReadOnlyList<ModelLabArchitectureTemplate> Templates { get; init; }

    /// <summary>The tensors outside any block, grouped as <c>embedding</c>, <c>output_norm</c>, <c>output</c> and <c>other</c>.</summary>
    public required IReadOnlyList<ModelLabArchitectureGroup> NonBlock { get; init; }
}

/// <summary>One GGUF block. Its index is the HF layer index for every block except an MTP block, which has no HF layer.</summary>
public sealed record ModelLabArchitectureBlock
{
    public required int Index { get; init; }

    /// <summary><c>fullAttention</c>, <c>linearAttention</c>, <c>mtp</c> or <c>other</c>, from the tensor names.</summary>
    public required string Kind { get; init; }

    /// <summary>The block's stored bytes, or null when any tensor's size is unknown.</summary>
    public required long? ByteSize { get; init; }

    public required IReadOnlyList<ModelLabArchitectureGroup> Groups { get; init; }
}

/// <summary>The tensors of one component, as read from the file.</summary>
public sealed record ModelLabArchitectureGroup
{
    public required string ComponentId { get; init; }

    /// <summary>
    ///     <c>attention</c>, <c>ssm</c>, <c>ffn</c>, <c>norm</c>, <c>mtp</c> or <c>other</c> in a block; <c>embedding</c>, <c>norm</c>,
    ///     <c>output</c> or <c>other</c> outside one.
    /// </summary>
    public required string Component { get; init; }

    /// <summary>Always <c>read</c>: the grouping comes from tensor names in the file.</summary>
    public required string Provenance { get; init; }

    public required IReadOnlyList<ModelLabArchitectureTensor> Tensors { get; init; }
}

/// <summary>One GGUF tensor and, for a supported architecture, the Hugging Face tensor it was converted from.</summary>
public sealed record ModelLabArchitectureTensor
{
    public required string GgufName { get; init; }

    /// <summary>The Hugging Face checkpoint's tensor name, or null when this tensor has none or the architecture is not mapped.</summary>
    public required string? HfName { get; init; }

    /// <summary>The dimensions in GGUF order (innermost first): the HF shape reversed.</summary>
    public required IReadOnlyList<long> Shape { get; init; }

    /// <summary>The ggml type name, or null for a type id the node does not know.</summary>
    public required string? GgmlType { get; init; }

    public required long? ByteSize { get; init; }

    /// <summary>
    ///     GGUF value per HF value: <c>equal</c>, <c>plus_one</c>, <c>neg_exp</c> (−exp), <c>reordered</c> (value heads permuted) or
    ///     <c>quantized</c> (not a float type); null when not stated.
    /// </summary>
    /// <remarks>
    ///     <c>reordered</c> wins over <c>quantized</c>: when the linear-attention key and value head counts differ, the converter
    ///     permutes the value heads before quantizing, so dequantized values still sit in another order. Null when
    ///     <see cref="HfName" /> is, or when the file does not state both head counts.
    /// </remarks>
    public required string? ValueRelation { get; init; }
}

/// <summary>The operation order of one block kind, inferred from llama.cpp rather than read from the file.</summary>
public sealed record ModelLabArchitectureTemplate
{
    /// <summary>The block kind it describes, as in <see cref="ModelLabArchitectureBlock.Kind" />.</summary>
    public required string Kind { get; init; }

    /// <summary>Always <c>inferred</c>.</summary>
    public required string Provenance { get; init; }

    /// <summary>The upstream file and llama.cpp tag the template follows.</summary>
    public required string Source { get; init; }

    public required IReadOnlyList<ModelLabArchitectureTemplateNode> Nodes { get; init; }

    public required IReadOnlyList<ModelLabArchitectureTemplateEdge> Edges { get; init; }
}

/// <summary>One operation of a template.</summary>
public sealed record ModelLabArchitectureTemplateNode
{
    public required string Id { get; init; }

    public required string Label { get; init; }

    /// <summary>The block component whose tensors this operation uses (a group's <c>Component</c> in the same block), or null.</summary>
    public required string? Component { get; init; }
}

/// <summary>Data flowing from one template node to another.</summary>
public sealed record ModelLabArchitectureTemplateEdge
{
    public required string From { get; init; }

    public required string To { get; init; }
}
