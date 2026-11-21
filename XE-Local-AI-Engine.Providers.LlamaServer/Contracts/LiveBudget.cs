namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>The live memory one chat admission may re-place a launch into: global free bytes and the ledger's in-flight reservations, per axis.</summary>
public sealed class LiveBudget
{
    /// <summary>Machine-global free VRAM as the admission just read it.</summary>
    public required long GlobalFreeGpuBytes { get; init; }

    /// <summary>VRAM the pending-footprint ledger holds for launches not yet resident.</summary>
    public required long ReservedGpuBytes { get; init; }

    /// <summary>Available system RAM as the admission just read it.</summary>
    public required long AvailableRamBytes { get; init; }

    /// <summary>RAM the pending-footprint ledger holds for launches not yet resident.</summary>
    public required long ReservedRamBytes { get; init; }
}
