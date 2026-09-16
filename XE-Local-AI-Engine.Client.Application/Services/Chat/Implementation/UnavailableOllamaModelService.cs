namespace XE_Local_AI_Engine.Client.Services.Chat.Implementation;

using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;
using XE_Local_AI_Engine.Providers.Ollama.Contracts;

/// <summary>
///     The <see cref="IOllamaModelService" /> the composition root registers when the optional Ollama runtime is gated
///     OFF (<c>XE_OLLAMA_RUNTIME_ENABLED=false</c>).
/// </summary>
/// <remarks>
///     <para>
///         WHY this exists: <c>OllamaModelService</c> takes an <c>IOllamaApiClient</c>, whose only registration
///         lives inside the gated <c>AddOllamaLocalModelProvider</c>. Registering the real service unconditionally
///         therefore broke a gate-off node — a Development host failed <c>ValidateOnBuild</c> outright, and any other
///         host threw at the first resolve of the model catalog, capacity, classification, model-fit or the three
///         local-model endpoints.
///     </para>
///     <para>
///         Behaviour mirrors a box with no Ollama daemon, which every consumer already handles: the two list probes
///         answer empty, availability is false, and each member that cannot answer without a daemon throws
///         <see cref="HttpRequestException" /> with no <see cref="HttpRequestException.StatusCode" /> — the exact
///         transport failure a refused connection produces, and the one
///         <c>GetLocalModelDetailsEndpoint</c>, <c>ModelClassificationService</c> and <c>UnloadLocalModelEndpoint</c>
///         already map to their "Ollama not reachable" outcome.
///     </para>
/// </remarks>
internal sealed class UnavailableOllamaModelService : IOllamaModelService
{
    public Task<IEnumerable<OllamaModelSummary>> ListLocalModelsAsync(CancellationToken ct = default) =>
        Task.FromResult(Enumerable.Empty<OllamaModelSummary>());

    public Task<OllamaModelDetails> ShowModelDetailsAsync(string modelName, CancellationToken ct = default) =>
        Task.FromException<OllamaModelDetails>(Unavailable());

    // Deliberately NOT an iterator: with no daemon there is nothing to stream, so the caller learns at the call
    // instead of at the first MoveNextAsync. No product code pulls through this service today.
    public IAsyncEnumerable<PullProgress> PullModelAsync(string modelName, CancellationToken ct = default) =>
        throw Unavailable();

    public Task DeleteModelAsync(string modelName, CancellationToken ct = default) =>
        Task.FromException(Unavailable());

    public Task<IReadOnlyList<RunningModelSnapshot>> ListRunningModelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RunningModelSnapshot>>([]);

    public Task UnloadModelAsync(string modelName, CancellationToken ct = default) =>
        Task.FromException(Unavailable());

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) =>
        Task.FromResult(false);

    // No daemon means no installed model, which is the same "ineligible" answer a remote or unreachable endpoint gives.
    public Task<bool> IsLoopbackModelInstalledAsync(string modelName, CancellationToken ct = default) =>
        Task.FromResult(false);

    private static HttpRequestException Unavailable() =>
        new($"The Ollama runtime is disabled on this node ({OllamaRuntimeGate.RuntimeEnabledConfigurationKey}=false).");
}
