namespace XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>VRAM usage of one GPU in a <see cref="LiveMemorySample" />, keyed by the driver's device index.</summary>
public sealed class GpuMemorySample
{
    public required int Index { get; init; }

    public required long TotalVramBytes { get; init; }

    public required long UsedVramBytes { get; init; }

    public required long AvailableVramBytes { get; init; }
}
