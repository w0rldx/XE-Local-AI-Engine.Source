namespace XE_Local_AI_Engine.Client.Services.Capabilities.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     The <see cref="IModelCapabilityClient" /> the composition root registers when the optional Ollama runtime is
///     gated OFF (<c>XE_OLLAMA_RUNTIME_ENABLED=false</c>).
/// </summary>
/// <remarks>
///     The gate skips <c>AddOllamaLocalModelProvider</c>, which holds the ONLY registration of <see cref="IModelCapabilityClient" />, so without a
///     substitute the container cannot activate <see cref="ModelCapabilityProber" /> and an operator who merely opted out of a SECONDARY runtime could
///     not start the node at all. Behaviour is deliberately "nothing to probe", NOT an error: every probe answers empty or negative instead of throwing,
///     so <see cref="CapabilityReporter" /> reports what a desktop with no Ollama daemon reports — an unreachable Ollama, the <c>ollama-unreachable</c>
///     diagnostic, an unknown management mode, the configured-model fallbacks as the installed set, and no active model. llama.cpp is unaffected.
/// </remarks>
internal sealed class UnavailableModelCapabilityClient : IModelCapabilityClient
{
    public Task<bool> IsRuntimeReachableAsync(CancellationToken ct) =>
        Task.FromResult(false);

    public Task<string?> GetRuntimeVersionAsync(CancellationToken ct) =>
        Task.FromResult<string?>(null);

    public Task<IReadOnlyList<InstalledModelEntry>> ListInstalledModelsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<InstalledModelEntry>>([]);

    public Task<IReadOnlyList<RunningModelSnapshot>> ListRunningModelsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<RunningModelSnapshot>>([]);

    public Task<ModelCapabilityDetail> GetModelDetailAsync(string modelName, CancellationToken ct) =>
        Task.FromResult(new ModelCapabilityDetail { MaxContextTokens = null });
}
