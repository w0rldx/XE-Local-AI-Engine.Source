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

    /// <summary>The process GPU budget, before its reserve, this allocation was priced at; <see langword="null" /> when it books no GPU.</summary>
    public long? GpuPlacementBudgetBytes { get; init; }

    /// <summary>The <c>--fit-target</c> margin a live-share Hybrid launches with, so llama.cpp aims at the booking; otherwise <see langword="null" />.</summary>
    public long? FitTargetBytes { get; init; }

    /// <summary>
    ///     <see langword="true" /> for a CPU allocation priced for a GPU variant from a timed-out GPU probe: never cached, and no later
    ///     substitution (commit, effective, down-tier, live placement) acts on it.
    /// </summary>
    public bool IsTransient { get; init; }
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
