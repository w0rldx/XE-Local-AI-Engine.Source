namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <param name="Name">Raw running-model name as reported by the runtime (caller normalizes).</param>
/// <param name="ModelName">Alternate model identifier the runtime may report instead of <paramref name="Name" />.</param>
/// <param name="ExpiresAt">When the loaded model is scheduled to be evicted, when the runtime reports it.</param>
/// <param name="SizeBytes">Total resident size of the loaded model in bytes (RAM + VRAM), when the runtime reports it.</param>
/// <param name="SizeVramBytes">Portion of the loaded model resident in GPU VRAM in bytes, when the runtime reports it.</param>
public sealed record RunningModelSnapshot(
    string? Name,
    string? ModelName,
    DateTimeOffset? ExpiresAt,
    long? SizeBytes = null,
    long? SizeVramBytes = null);
