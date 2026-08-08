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
/// <param name="ModelName">The model whose load produced this observation.</param>
/// <param name="Role">The role the observed process serves.</param>
/// <param name="OffloadedLayers">Layers the runtime placed on the GPU.</param>
/// <param name="TotalLayers">Total layers in the model (always positive).</param>
public sealed record LlamaLayerPlacement(string ModelName, ModelRole Role, int OffloadedLayers, int TotalLayers)
{
    /// <summary>
    ///     <see langword="true" /> when some layers stayed in system RAM. Serving still works; throughput does not.
    ///     This is NOT a CPU fallback — the GPU is in use, just not for the whole model.
    /// </summary>
    public bool IsPartial => OffloadedLayers < TotalLayers;
}
