namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;

using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

internal static class RuntimeResourcesMapper
{
    public static RuntimeResourcesResponse ToResponse(this LiveMemorySample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        return new RuntimeResourcesResponse
        {
            TotalRamBytes = sample.TotalRamBytes,
            AvailableRamBytes = sample.AvailableRamBytes,
            Gpus =
            [
                .. sample.Gpus.Select(static gpu => new GpuMemoryResponse
                {
                    Index = gpu.Index,
                    TotalVramBytes = gpu.TotalVramBytes,
                    UsedVramBytes = gpu.UsedVramBytes,
                    AvailableVramBytes = gpu.AvailableVramBytes
                })
            ]
        };
    }
}
