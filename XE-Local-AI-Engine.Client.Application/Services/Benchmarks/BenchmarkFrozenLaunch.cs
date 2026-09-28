namespace XE_Local_AI_Engine.Client.Services.Benchmarks;

using XE_Local_AI_Engine.Client.Persistence.Stores;

/// <summary>One phase's frozen launch vector plus the intent scalars the row records beside it.</summary>
public sealed class BenchmarkFrozenLaunch
{
    public required BenchmarkLlamaRuntimeSnapshotV1 Runtime { get; init; }

    public required BenchmarkRunLaunchIntent Intent { get; init; }
}
