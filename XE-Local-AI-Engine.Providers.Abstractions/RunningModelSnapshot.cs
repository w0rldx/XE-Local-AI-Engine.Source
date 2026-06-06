namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>A model the runtime currently reports as loaded/running.</summary>
/// <param name="Name">Raw running-model name as reported by the runtime (caller normalizes).</param>
/// <param name="ModelName">Alternate model identifier the runtime may report instead of <paramref name="Name" />.</param>
/// <param name="ExpiresAt">When the loaded model is scheduled to be evicted, when the runtime reports it.</param>
public sealed record RunningModelSnapshot(string? Name, string? ModelName, DateTimeOffset? ExpiresAt);
