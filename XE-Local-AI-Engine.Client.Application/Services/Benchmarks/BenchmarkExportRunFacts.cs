namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

public sealed class BenchmarkExportRunFacts
{
    public required string? BuildCommit { get; init; }

    public required string? GpuInfo { get; init; }

    public required string? ModelFilename { get; init; }

    public required long? ModelSizeBytes { get; init; }

    public required int? GpuLayers { get; init; }

    public static BenchmarkExportRunFacts Empty { get; } = new()
    {
        BuildCommit = null,
        GpuInfo = null,
        ModelFilename = null,
        ModelSizeBytes = null,
        GpuLayers = null
    };
}
