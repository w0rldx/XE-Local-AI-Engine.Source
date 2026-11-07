namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>What <c>modellab.py</c> reads for a source job. <see cref="ConfigDirectory" /> holds only <c>config.json</c>; nothing here is secret.</summary>
public sealed record ModelLabSourceJobV1
{
    public int ContractVersion { get; init; } = ModelLabTraceSpec.ContractVersion;

    public string Kind { get; init; } = "source";

    public required string ConfigDirectory { get; init; }

    public required string ResultPath { get; init; }

    public int MaxResultBytes { get; init; } = ModelLabTraceSpec.MaxResultBytes;
}

/// <summary>
///     A model's PyTorch module tree and per-block op graphs, from <c>result.json</c> and the cache. Built on the meta device, so it follows
///     the PyTorch reference path: a GPU run may use fused kernels the graph does not show.
/// </summary>
public sealed record ModelLabPyTorchIndex
{
    public string TransformersVersion { get; init; } = string.Empty;

    public string TorchVersion { get; init; } = string.Empty;

    public string ModelType { get; init; } = string.Empty;

    /// <summary><c>text_config.model_type</c>, or null when the config has none.</summary>
    public string? TextModelType { get; init; }

    public string ModelClass { get; init; } = string.Empty;

    /// <summary>
    ///     The config's <c>architectures</c>, read by the node from the config itself, or null when it names none. When it does not hold
    ///     <see cref="ModelClass" />, the index shows the model through another class, with another head.
    /// </summary>
    public IReadOnlyList<string>? DeclaredArchitectures { get; init; }

    /// <summary>Where the blocks sit, such as <c>model.layers</c> or <c>backbone.layers</c>.</summary>
    public string BlockPath { get; init; } = string.Empty;

    /// <summary><c>representative</c> (one block exported per layer kind and structure) or <c>full</c>.</summary>
    public string ExportMode { get; init; } = string.Empty;

    /// <summary>False when a node's line is its call site, because the hook that names a helper's own line was unavailable.</summary>
    public bool AnchoredLines { get; init; }

    public IReadOnlyList<ModelLabPyTorchBlock> Blocks { get; init; } = [];

    /// <summary>Every module outside the blocks: embedding, final norm, head, rotary.</summary>
    public IReadOnlyList<ModelLabPyTorchModule> Outside { get; init; } = [];

    /// <summary>The graph of the ops outside every block, or null.</summary>
    public string? OutsideGraphId { get; init; }

    /// <summary>Empty when the export failed; <see cref="ExportError" /> then says why, and the module links still hold.</summary>
    public IReadOnlyList<ModelLabPyTorchGraph> Graphs { get; init; } = [];

    public string? ExportError { get; init; }

    /// <summary>Every file a module or node names. Only these are served, and only while their hash still matches.</summary>
    public IReadOnlyList<ModelLabPyTorchFile> Files { get; init; } = [];
}

/// <summary>One block of the PyTorch model, at its index in the block list.</summary>
public sealed record ModelLabPyTorchBlock
{
    public int Index { get; init; }

    public string Path { get; init; } = string.Empty;

    public string Class { get; init; } = string.Empty;

    /// <summary>The config's <c>layer_types</c> entry, or null.</summary>
    public string? LayerType { get; init; }

    public string? GraphId { get; init; }

    public IReadOnlyList<ModelLabPyTorchModule> Modules { get; init; } = [];
}

/// <summary>One module and where its class is defined.</summary>
public sealed record ModelLabPyTorchModule
{
    /// <summary>The absolute module path, such as <c>model.layers.15.self_attn.q_proj</c>.</summary>
    public string Path { get; init; } = string.Empty;

    public string Class { get; init; } = string.Empty;

    /// <summary>Relative to the transformers package, or prefixed <c>torch:</c> for the torch package; null when it has no source file.</summary>
    public string? File { get; init; }

    public int? ClassStart { get; init; }

    public int? ClassEnd { get; init; }

    public int? ForwardLine { get; init; }
}

/// <summary>A block's ops grouped by module and source line.</summary>
public sealed record ModelLabPyTorchGraph
{
    public string GraphId { get; init; } = string.Empty;

    public IReadOnlyList<ModelLabPyTorchGraphNode> Nodes { get; init; } = [];

    /// <summary>Each edge as <c>[fromId, toId]</c>.</summary>
    public IReadOnlyList<IReadOnlyList<string>> Edges { get; init; } = [];
}

public sealed record ModelLabPyTorchGraphNode
{
    public string Id { get; init; } = string.Empty;

    /// <summary>Relative to the block; absolute in the outside graph.</summary>
    public string ModulePath { get; init; } = string.Empty;

    /// <summary>Null, with <see cref="Line" />, for an op with no stack trace.</summary>
    public string? File { get; init; }

    public int? Line { get; init; }

    public int OpCount { get; init; }

    /// <summary>The source line, stripped, at most 80 characters.</summary>
    public string Label { get; init; } = string.Empty;
}

public sealed record ModelLabPyTorchFile
{
    public string File { get; init; } = string.Empty;

    public string Sha256 { get; init; } = string.Empty;
}

/// <summary>Where an index's <c>config.json</c> came from.</summary>
public sealed record ModelLabPyTorchConfigSource
{
    /// <summary><c>checkpoint</c> or <c>huggingface</c>.</summary>
    public string Kind { get; init; } = string.Empty;

    public string RepoId { get; init; } = string.Empty;

    /// <summary>The commit sha.</summary>
    public string Revision { get; init; } = string.Empty;

    /// <summary>The downloaded checkpoint, for <c>checkpoint</c>.</summary>
    public Guid? BaseArtifactId { get; init; }
}

/// <summary>The last index built for one installed model, kept beside the cache.</summary>
public sealed record ModelLabPyTorchPointer
{
    public string IndexId { get; init; } = string.Empty;

    public ModelLabPyTorchConfigSource Source { get; init; } = new();
}

/// <summary>A model's PyTorch index, joined to its GGUF descriptor.</summary>
public sealed record ModelLabPyTorchIndexResponse
{
    /// <summary>The cache key, and what the source read names: config hash, transformers and torch versions, contract version.</summary>
    public required string IndexId { get; init; }

    public required string ModelName { get; init; }

    public required ModelLabPyTorchConfigSource Source { get; init; }

    public required ModelLabPyTorchIndex Index { get; init; }

    public required ModelLabPyTorchJoin Join { get; init; }
}

/// <summary>How GGUF components map to PyTorch modules. A group with no link has no code link: a missed rule is never a guess.</summary>
public sealed record ModelLabPyTorchJoin
{
    /// <summary>True when the GGUF's trunk blocks and the PyTorch blocks have the same count, so block N is layer N.</summary>
    public required bool BlocksJoined { get; init; }

    /// <summary><c>blockCountMismatch</c> or <c>ggufUnreadable</c> when the blocks are not joined, else null.</summary>
    public required string? Reason { get; init; }

    /// <summary>GGUF blocks other than an MTP block, which has no PyTorch layer.</summary>
    public required int GgufTrunkBlockCount { get; init; }

    public required int PyTorchBlockCount { get; init; }

    public required IReadOnlyList<ModelLabPyTorchComponentLink> Components { get; init; }

    public required IReadOnlyList<ModelLabPyTorchTensorLink> Tensors { get; init; }
}

/// <summary>A GGUF group's modules: block children for a block group, outside modules for embedding, norm and output.</summary>
public sealed record ModelLabPyTorchComponentLink
{
    public required string ComponentId { get; init; }

    public required IReadOnlyList<string> ModulePaths { get; init; }
}

/// <summary>A GGUF tensor's owning module, through its Hugging Face name.</summary>
public sealed record ModelLabPyTorchTensorLink
{
    public required string GgufName { get; init; }

    public required string ModulePath { get; init; }
}

/// <summary>What to build an index from.</summary>
public sealed record ModelLabPyTorchBuildRequest
{
    public required string ModelName { get; init; }

    /// <summary><c>checkpoint</c> (with <see cref="BaseArtifactId" />) or <c>huggingface</c> (with <see cref="RepoId" />).</summary>
    public required string Source { get; init; }

    public Guid? BaseArtifactId { get; init; }

    public string? RepoId { get; init; }

    public string? Revision { get; init; }
}

/// <summary>Why an index or a source file was not served. The value is the API's stable refusal code.</summary>
public enum ModelLabPyTorchRefusal
{
    UnsupportedPlatform,
    RuntimeUnavailable,
    CheckpointNotFound,
    CheckpointNotReady,
    ConfigMissing,
    ConfigTooLarge,
    ConfigUnreadable,
    RemoteCode,
    UnsupportedModelType,
    NoBlocks,
    ResultTooLarge,
    RepoUnreachable,
    RepoGated,
    Busy,
    IndexFailed,
    SourceNotInIndex,
    SourceChanged,
    SourceTooLarge
}

public sealed record ModelLabPyTorchOutcome
{
    public ModelLabPyTorchIndexResponse? Index { get; init; }

    public ModelLabPyTorchRefusal? Refusal { get; init; }

    public string? RefusalMessage { get; init; }
}

/// <summary>One served source file.</summary>
public sealed record ModelLabPyTorchSourceFile
{
    public required string File { get; init; }

    public required string Sha256 { get; init; }

    public required string Text { get; init; }
}

public sealed record ModelLabPyTorchSourceRead
{
    public ModelLabPyTorchSourceFile? Source { get; init; }

    public ModelLabPyTorchRefusal? Refusal { get; init; }

    public string? RefusalMessage { get; init; }
}

/// <summary>No index has been built for this model on the installed runtime.</summary>
public sealed class ModelLabPyTorchIndexNotFoundException : ResourceNotFoundException
{
    public ModelLabPyTorchIndexNotFoundException(string modelName) : base($"No PyTorch index has been built for '{modelName}' on the installed runtime.")
    {
    }
}
