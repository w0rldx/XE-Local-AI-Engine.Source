namespace XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>
///     Bounds for sub-agent spawning, bound from the <c>Spawn</c> configuration section. Defaults: depth ≤ 2
///     structural; fan-out ≤ 3 concurrent per root invocation; cloud-spawn ≤ 3 per root invocation; a bounded
///     same-model queue wait.
/// </summary>
public sealed class SpawnOptions
{
    public const string SectionName = "Spawn";

    /// <summary>Maximum concurrent live sub-agents per root invocation (fan-out cap).</summary>
    public int MaxConcurrentSpawns { get; set; } = 3;

    /// <summary>Maximum cloud-bound sub-agent spawns per root invocation (DoS-of-wallet cap).</summary>
    public int MaxCloudSpawns { get; set; } = 3;

    /// <summary>Bounded wait for a same-model serialized turn before the spawn is rejected as "busy".</summary>
    public int QueueWaitSeconds { get; set; } = 120;
}
