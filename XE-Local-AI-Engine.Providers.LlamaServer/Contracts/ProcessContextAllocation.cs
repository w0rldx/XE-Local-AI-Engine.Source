namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

using System.Runtime.InteropServices;

/// <summary>The immutable, shared process-context decision used by both admission sizing and llama-server launch.</summary>
public sealed record ProcessContextAllocation
{
    public required int ProcessContextTokens { get; init; }

    public required int? ModelTrainContextTokens { get; init; }

    public required ProcessContextAllocationSource Source { get; init; }

    public required ProcessPlacementMode Placement { get; init; }

    public required ResourceFootprint Footprint { get; init; }

    public required string ContentIdentity { get; init; }

    public required string CacheKey { get; init; }

    /// <summary>
    ///     The slot count a chat process launches with: <see cref="LlamaServerChatSlots.Count" />, or 1 when the second slot
    ///     would cost window or residency. Ignored for every other role, which always runs one slot.
    /// </summary>
    public int ChatSlots { get; init; } = LlamaServerChatSlots.Count;
}

/// <summary>Dual-axis resources reserved for a process.</summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct ResourceFootprint(long GpuBytes, long RamBytes)
{
    public static ResourceFootprint Zero { get; } = new(0, 0);
}

public enum ProcessContextAllocationSource
{
    FrozenProfile = 0,
    DeterministicOverride = 1,
    HardwareTier = 2
}

public enum ProcessPlacementMode
{
    Cpu = 0,
    GpuResident = 1,
    Hybrid = 2,
    ExpertOffload = 3
}
