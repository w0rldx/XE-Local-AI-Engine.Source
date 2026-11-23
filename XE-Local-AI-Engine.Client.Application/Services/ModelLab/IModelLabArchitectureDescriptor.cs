namespace XE_Local_AI_Engine.Client.Services.ModelLab;

using XE_Local_AI_Engine.Providers.Abstractions.Gguf;

/// <summary>What one GGUF architecture adds to the shared descriptor: HF tensor names and llama.cpp operation templates.</summary>
internal interface IModelLabArchitectureDescriptor
{
    /// <summary>The GGUF <c>general.architecture</c> value it describes.</summary>
    string Architecture { get; }

    /// <summary>The llama.cpp tag its tables and templates were checked against.</summary>
    string LlamaCppTag { get; }

    /// <summary>The HF tensor a block tensor was converted from, or null for a name the table does not hold.</summary>
    /// <param name="blockKind">The block's kind from <see cref="ModelLabArchitectureBuilder.BlockKind" />: one GGUF name can come from different HF tensors per kind.</param>
    ModelLabHfTensor? BlockHfName(string ggufName, int blockIndex, string blockKind, GgufTensorInspection inspection);

    /// <summary>The HF tensor a tensor outside the blocks was converted from, or null for a name the table does not hold.</summary>
    ModelLabHfTensor? NonBlockHfName(string ggufName);

    /// <summary>The operation templates for the block kinds present; empty for a layout the templates do not describe.</summary>
    IReadOnlyList<ModelLabArchitectureTemplate> TemplatesFor(IReadOnlyList<ModelLabArchitectureBlock> blocks, GgufTensorInspection inspection);
}
