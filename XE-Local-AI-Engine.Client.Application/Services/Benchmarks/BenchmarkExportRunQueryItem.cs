namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed class BenchmarkExportRunQueryItem
{
    public required BenchmarkRunRecord Summary { get; init; }

    public required BenchmarkRunRecord Full { get; init; }

    public required BenchmarkJudgeResultV2? Verdict { get; init; }
}
