namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Provider-neutral probing surface for runtime and per-model capability detection that the
///     <see cref="ILocalModelProvider" /> management contract does not expose.
/// </summary>
/// <remarks>
///     This narrows the raw capability probes (runtime reachability/version, installed-model digests, running models,
///     per-model context length) into provider-neutral snapshots so application-layer capability reporting no longer
///     binds the concrete runtime client. Implementations are thin pass-throughs and intentionally do NOT swallow
///     transport failures: a probe against an unreachable runtime propagates the provider's transport exception (for
///     example <see cref="System.Net.Http.HttpRequestException" />) so callers can classify unreachability exactly as
///     they would against the raw client.
/// </remarks>
public interface IModelCapabilityClient
{
    /// <summary>Checks whether the model-runtime endpoint is currently reachable.</summary>
    Task<bool> IsRuntimeReachableAsync(CancellationToken ct);

    /// <summary>Reads the runtime version string, or <c>null</c> when the runtime does not report one.</summary>
    Task<string?> GetRuntimeVersionAsync(CancellationToken ct);

    /// <summary>Lists the locally installed models with their provider digests, without per-model probing.</summary>
    Task<IReadOnlyList<InstalledModelEntry>> ListInstalledModelsAsync(CancellationToken ct);

    /// <summary>Lists the models the runtime currently reports as loaded/running.</summary>
    Task<IReadOnlyList<RunningModelSnapshot>> ListRunningModelsAsync(CancellationToken ct);

    /// <summary>Probes a single model for its capability detail (for example its maximum context length).</summary>
    Task<ModelCapabilityDetail> GetModelDetailAsync(string modelName, CancellationToken ct);
}
