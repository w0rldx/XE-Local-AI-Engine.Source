namespace XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     The node's record of measured GPU layer placement (see <see cref="LlamaLayerPlacement" />), written by the
///     process supervisor as models load and read by the operator-facing runtime audit.
/// </summary>
/// <remarks>
///     EVERY GPU spawn is observed, not just the first per model. Placement under llama.cpp auto-fit is decided
///     against the FREE VRAM at load time, so the same model can be fully resident when loaded alone and only partly
///     resident when loaded beside others — a remembered answer would go stale exactly when it matters. Each entry is
///     therefore overwritten by the newest load of its <c>(model, role, variant)</c>.
/// </remarks>
public interface ILlamaLayerPlacementReport
{
    /// <summary>
    ///     The placement observation to show an operator, or <see langword="null" /> when no model has been loaded and
    ///     observed yet. A PARTIAL observation is preferred over a full one, so loading a small model that fits cannot
    ///     mask a large model that is spilling layers to system RAM; ties break toward the most recent observation.
    ///     <para>
    ///         That preference is only honest because <see cref="Remove" /> is called as each process is torn down, so
    ///         every reading still held describes a model that is loaded RIGHT NOW. An unremoved partial would outrank
    ///         every full reading forever, whatever its age.
    ///     </para>
    /// </summary>
    LlamaLayerPlacement? Current { get; }

    /// <summary>Records what a load of this <c>(model, role, variant)</c> measurably did, replacing any prior reading for it.</summary>
    void Record(ModelRole role, GpuVariant variant, string modelName, int offloadedLayers, int totalLayers);

    /// <summary>
    ///     Drops every reading for this <c>(model, role)</c> because the process that produced it is gone — evicted,
    ///     ejected, crashed, reaped, or replaced.
    ///     <para>
    ///         Keyed on <c>(model, role)</c> and not on the recorded <c>(model, role, variant)</c> deliberately: the
    ///         supervisor tears a process down without carrying the llama.cpp build it was launched against, and a
    ///         re-load that selects a DIFFERENT variant must not leave the previous variant's reading behind to be
    ///         reported alongside — or instead of — the model that is actually resident.
    ///     </para>
    /// </summary>
    void Remove(ModelRole role, string modelName);
}
