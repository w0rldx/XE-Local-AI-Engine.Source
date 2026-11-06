namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

/// <summary>Persisted shape for <see cref="LlamaServerLaunchFallbackStore" />: the launch configs proven unable to reach readiness.</summary>
/// <param name="DisabledOptimizedConfigs">
///     <c>"{Variant}:{kvType}"</c> keys whose KV-quant + flash-attention config failed readiness. A <c>q4_0</c> failure
///     here leaves <c>q8_0</c> on the same backend enabled.
/// </param>
public sealed record LlamaServerLaunchFallbackState(IReadOnlyList<string>? DisabledOptimizedConfigs = null);
