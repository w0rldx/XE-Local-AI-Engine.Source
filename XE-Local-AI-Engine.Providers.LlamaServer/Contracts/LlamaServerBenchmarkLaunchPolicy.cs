namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Deterministic benchmark-only server settings that never inherit mutable chat tuning.</summary>
public sealed record LlamaServerBenchmarkLaunchPolicy(
    int Version,
    int ChatCacheReuse,
    int ChatCacheRamMiB,
    bool SpeculativeDecodingEnabled)
{
    public static LlamaServerBenchmarkLaunchPolicy DeterministicV1 { get; } = new(1, 0, 0, false);

    public bool IsSupported => this == DeterministicV1;
}
