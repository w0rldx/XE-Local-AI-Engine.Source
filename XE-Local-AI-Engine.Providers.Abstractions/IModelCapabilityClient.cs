namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Provider-neutral probing surface for runtime and per-model capability detection that the
///     <see cref="ILocalModelProvider" /> management contract does not expose.
/// </summary>
/// <remarks>
///     This narrows the raw capability probes (runtime reachability, installed-model digests) into provider-neutral snapshots so application-layer capability reporting does not bind the concrete runtime client.
///     Implementations are thin pass-throughs and intentionally do NOT swallow transport failures: a probe against an unreachable
///     runtime propagates the provider's transport exception (for example <see cref="System.Net.Http.HttpRequestException" />) so
///     callers can classify unreachability exactly as they would against the raw client.
/// </remarks>
public interface IModelCapabilityClient
{
    /// <summary>Checks whether the model-runtime endpoint is currently reachable.</summary>
    Task<bool> IsRuntimeReachableAsync(CancellationToken ct);

    /// <summary>Lists the locally installed models with their provider digests, without per-model probing.</summary>
    Task<IReadOnlyList<InstalledModelEntry>> ListInstalledModelsAsync(CancellationToken ct);
}
