namespace XE_Local_AI_Engine.Client.Services.Chat;

using OllamaSharp.Models;
using XE_Local_AI_Engine.Providers.Abstractions;

public interface IOllamaModelService
{
    Task<IEnumerable<Model>> ListLocalModelsAsync(CancellationToken ct = default);
    Task<ShowModelResponse> ShowModelAsync(string modelName, CancellationToken ct = default);
    Task<OllamaModelDetails> ShowModelDetailsAsync(string modelName, CancellationToken ct = default);
    IAsyncEnumerable<PullModelResponse> PullModelAsync(string modelName, CancellationToken ct = default);
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
}
