namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

/// <summary>Live whole-machine memory usage (<c>GET model-fit/resources</c>); aggregates only, no machine identifiers.</summary>
public sealed class RuntimeResourcesResponse
{
    public required long TotalRamBytes { get; init; }

    public required long AvailableRamBytes { get; init; }

    /// <summary>One entry per measured GPU; empty when VRAM usage is unknown.</summary>
    public required IReadOnlyList<GpuMemoryResponse> Gpus { get; init; }
}

/// <summary>VRAM usage of one GPU, keyed by the driver's device index; no device name or serial.</summary>
public sealed class GpuMemoryResponse
{
    public required int Index { get; init; }

    public required long TotalVramBytes { get; init; }

    public required long UsedVramBytes { get; init; }

    public required long AvailableVramBytes { get; init; }
}
