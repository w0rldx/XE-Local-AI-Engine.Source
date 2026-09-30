namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

/// <summary>Persisted shape for <see cref="LlamaServerLaunchFallbackStore" />: the launch configs proven unable to reach readiness.</summary>
/// <param name="DisabledOptimizedVariants">
///     LEGACY backend names (<see cref="GpuVariant" />), kept only so an old file still deserializes: ignored, dropped
///     on the first read, always written empty.
/// </param>
/// <param name="DisabledOptimizedConfigs">
///     <c>"{Variant}:{kvType}"</c> keys whose KV-quant + flash-attention config failed readiness. A <c>q4_0</c> failure
///     here leaves <c>q8_0</c> on the same backend enabled.
/// </param>
public sealed record LlamaServerLaunchFallbackState(
    IReadOnlyList<string> DisabledOptimizedVariants,
    IReadOnlyList<string>? DisabledOptimizedConfigs = null);
