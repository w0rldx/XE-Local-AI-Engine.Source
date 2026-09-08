namespace XE_Local_AI_Engine.Client.Services.Capabilities.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     The <see cref="IModelCapabilityClient" /> the composition root registers when the optional Ollama runtime is
///     gated OFF (<c>XE_OLLAMA_RUNTIME_ENABLED=false</c>).
/// </summary>
/// <remarks>
///     <para>
///         WHY this exists: the gate skips <c>AddOllamaLocalModelProvider</c>, which holds the ONLY registration of
///         <see cref="IModelCapabilityClient" />. Without a substitute the container cannot activate
///         <see cref="ModelCapabilityProber" /> and the host fails to build — an operator who merely opted out of a
///         SECONDARY runtime could not start the node at all.
///     </para>
///     <para>
///         Behaviour is deliberately "nothing to probe", NOT an error: every probe answers empty/negative instead of
///         throwing. With it in place <see cref="CapabilityReporter" /> reports exactly what a desktop with no Ollama
///         daemon reports today — <c>OllamaReachable=false</c>, the <c>ollama-unreachable</c> diagnostic,
///         <c>ManagementMode=unknown</c>, the configured-model fallbacks as the installed set, and no active model.
///         llama.cpp remains the node's real runtime and is unaffected.
///     </para>
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
        Task.FromResult(new ModelCapabilityDetail(MaxContextTokens: null));
}
