namespace XE_Local_AI_Engine.Providers.Ollama.Contracts;

using XE_Local_AI_Engine.Providers.Abstractions;
using XE_Local_AI_Engine.Providers.Abstractions.Contracts;

/// <summary>
///     Model management against the Ollama runtime. Declared inside this provider (not in
///     <c>Providers.Abstractions</c>) because it is an Ollama-only contract: no other provider implements it, and the
///     implementation lives in the same assembly, so no project edge is reversed.
/// </summary>
public interface IOllamaModelService
{
    Task<IEnumerable<OllamaModelSummary>> ListLocalModelsAsync(CancellationToken ct = default);
    Task<OllamaModelDetails> ShowModelDetailsAsync(string modelName, CancellationToken ct = default);
    IAsyncEnumerable<PullProgress> PullModelAsync(string modelName, CancellationToken ct = default);
    Task DeleteModelAsync(string modelName, CancellationToken ct = default);

    /// <summary>
    ///     Lists the models the runtime currently holds in memory (RAM/VRAM), as provider-neutral snapshots. Used by the
    ///     loaded-models overview so transport types stay out of the endpoint layer.
    /// </summary>
    Task<IReadOnlyList<RunningModelSnapshot>> ListRunningModelsAsync(CancellationToken ct = default);

    /// <summary>
    ///     Requests a graceful in-memory unload of the named model (<c>keep_alive=0</c>). An in-flight generation completes
    ///     first; the model is evicted afterwards. Unloading a model that is not loaded is a no-op success (idempotent).
    /// </summary>
    Task UnloadModelAsync(string modelName, CancellationToken ct = default);

    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    /// <summary>
    ///     Whether the named model is installed on a runtime this node reaches over loopback. The loopback check is the
    ///     same fact the composition-time SSRF guard enforces, read without throwing: a remote endpoint, a disabled
    ///     runtime or an unreachable daemon all answer <see langword="false" />.
    /// </summary>
    Task<bool> IsLoopbackModelInstalledAsync(string modelName, CancellationToken ct = default);
}
