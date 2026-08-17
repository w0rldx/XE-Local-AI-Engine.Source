namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using System.Collections.Concurrent;

/// <summary>
///     Default in-memory <see cref="ILlamaLayerPlacementReport" />. Process-lifetime singleton, shared by the process
///     supervisor (writer) and the runtime device audit (reader). Deliberately not persisted: placement depends on the
///     binary, the free VRAM at load time, and the launch plan, so a value carried across a restart could be wrong in
///     exactly the situation it exists to expose. For the same reason each load overwrites its key rather than being
///     recorded once — a reload under different VRAM pressure can legitimately place layers differently.
/// </summary>
internal sealed class LlamaLayerPlacementReport : ILlamaLayerPlacementReport
{
    private readonly ConcurrentDictionary<ObservationKey, Observation> _observations = new();
    private long _sequence;

    /// <inheritdoc />
    /// <remarks>
    ///     Prefers the newest PARTIAL observation, then the newest observation of any kind. Without the preference,
    ///     loading a small embedding model after a large chat model that spilled layers would replace the actionable
    ///     "38/49 on GPU" with a reassuring "13/13 on GPU" for a model nobody is waiting on. The ranking key encodes
    ///     exactly that priority; sequence numbers are unique, so the maximum is never ambiguous.
    ///     <para>
    ///         The preference is absolute — a partial outranks a full reading whatever their sequence numbers — and it
    ///         is only defensible because <see cref="Remove" /> retires a reading the moment its process is torn down.
    ///         Ranking cannot substitute for that: the alternative, letting a newer full reading win on sequence, would
    ///         restore exactly the masking the preference exists to prevent, because two models really can be resident
    ///         at once with only the older one spilling.
    ///     </para>
    /// </remarks>
    public LlamaLayerPlacement? Current =>
        _observations.Values
                     .MaxBy(static observation => (observation.Placement.IsPartial, observation.Sequence))
                     ?.Placement;

    /// <inheritdoc />
    public void Record(ModelRole role, GpuVariant variant, string modelName, int offloadedLayers, int totalLayers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);
        ArgumentOutOfRangeException.ThrowIfNegative(offloadedLayers);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalLayers);

        var placement = new LlamaLayerPlacement(modelName, role, offloadedLayers, totalLayers);
        var observation = new Observation(placement, Interlocked.Increment(ref _sequence));
        _observations[new ObservationKey(modelName, role, variant)] = observation;
    }

    /// <inheritdoc />
    public void Remove(ModelRole role, string modelName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelName);

        // Every variant recorded under this (model, role) goes, because the caller tearing the process down does not
        // know which build produced the reading. Keys snapshots the dictionary, so removing while iterating is safe.
        foreach (var key in _observations.Keys)
        {
            if (key.Role == role && string.Equals(key.ModelName, modelName, StringComparison.OrdinalIgnoreCase))
            {
                _ = _observations.TryRemove(key, out _);
            }
        }
    }

    private readonly record struct ObservationKey(string ModelName, ModelRole Role, GpuVariant Variant)
    {
        public bool Equals(ObservationKey other) =>
            Role == other.Role
            && Variant == other.Variant
            && string.Equals(ModelName, other.ModelName, StringComparison.OrdinalIgnoreCase);

        public override int GetHashCode() =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(ModelName), Role, Variant);
    }

    private sealed record Observation(LlamaLayerPlacement Placement, long Sequence);
}
