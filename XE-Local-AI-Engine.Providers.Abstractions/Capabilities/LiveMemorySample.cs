namespace XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>One live reading of whole-machine RAM and per-GPU VRAM; aggregates only, no device names or identifiers.</summary>
public sealed class LiveMemorySample
{
    public required long TotalRamBytes { get; init; }

    /// <summary>RAM available for allocation in bytes (free + reclaimable).</summary>
    public required long AvailableRamBytes { get; init; }

    /// <summary>One entry per measured GPU; empty when VRAM could not be measured, never zero-filled.</summary>
    public required IReadOnlyList<GpuMemorySample> Gpus { get; init; }
}
