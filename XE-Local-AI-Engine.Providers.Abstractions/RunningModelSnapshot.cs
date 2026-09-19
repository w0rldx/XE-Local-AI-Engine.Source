namespace XE_Local_AI_Engine.Providers.Abstractions;

public sealed class RunningModelSnapshot
{
    /// <summary>Raw running-model name as reported by the runtime (caller normalizes).</summary>
    public required string? Name { get; init; }

    /// <summary>Alternate model identifier the runtime may report instead of <see cref="Name" />.</summary>
    public required string? ModelName { get; init; }

    /// <summary>When the loaded model is scheduled to be evicted, when the runtime reports it.</summary>
    public required DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>Total resident size of the loaded model in bytes (RAM + VRAM), when the runtime reports it.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>Portion of the loaded model resident in GPU VRAM in bytes, when the runtime reports it.</summary>
    public long? SizeVramBytes { get; init; }
}
