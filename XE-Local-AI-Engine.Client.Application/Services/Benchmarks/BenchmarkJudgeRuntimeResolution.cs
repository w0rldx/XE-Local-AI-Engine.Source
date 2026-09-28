namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Stores;

public sealed class BenchmarkJudgeRuntimeResolution
{
    public required BenchmarkJudgeRuntimeV1 Runtime { get; init; }

    public required BenchmarkRunLaunchIntent Intent { get; init; }
}
