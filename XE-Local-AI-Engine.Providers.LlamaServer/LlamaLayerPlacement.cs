namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     What a real model load actually did with a model's layers: how many of <see cref="TotalLayers" /> the runtime
///     placed on the GPU. Read from llama.cpp's own <c>load_tensors: offloaded N/M layers to GPU</c> banner, so it is
///     measured rather than inferred.
/// </summary>
/// <remarks>
///     This is a per-MODEL fact and is deliberately distinct from the node-level device audit. The device audit answers
///     "can the selected binary see a GPU at all"; this answers "did THIS model's weights fit on it". Both can be
///     healthy-looking while the second is partial: on a box whose VRAM cannot hold the whole model, llama.cpp's
///     auto-fit spills the remaining layers to system RAM and serves correctly, just several times slower.
/// </remarks>
public sealed record LlamaLayerPlacement
{
    /// <summary>The model whose load produced this observation.</summary>
    public required string ModelName { get; init; }

    /// <summary>The role the observed process serves.</summary>
    public required ModelRole Role { get; init; }

    /// <summary>Layers the runtime placed on the GPU.</summary>
    public required int OffloadedLayers { get; init; }

    /// <summary>Total layers in the model (always positive).</summary>
    public required int TotalLayers { get; init; }

    /// <summary>
    ///     <see langword="true" /> when some layers stayed in system RAM. Serving still works; throughput does not.
    ///     This is NOT a CPU fallback — the GPU is in use, just not for the whole model.
    /// </summary>
    public bool IsPartial => OffloadedLayers < TotalLayers;
}
