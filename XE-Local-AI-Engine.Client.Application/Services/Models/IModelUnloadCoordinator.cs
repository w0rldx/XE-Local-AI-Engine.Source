namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Gracefully evicts a model from the local runtimes' memory, wherever it is resident.</summary>
/// <remarks>
///     BOTH runtimes are asked, because the node cannot know which one holds the model: residency is a property of a running process, while
///     the per-model provider map only records where a model would be <em>served</em>, and a model pulled into Ollama has no map row at all —
///     so routing this action by that map ejects nothing and reports success while Ollama still holds the weights. Both paths let an
///     in-flight generation complete before the model is evicted, so unload never interrupts a running turn.
/// </remarks>
public interface IModelUnloadCoordinator
{
    /// <summary>
    ///     Unloads <paramref name="modelName" /> from every local runtime that may hold it. Idempotent: unloading a model that is not
    ///     loaded still reports success.
    /// </summary>
    /// <remarks>
    ///     <see cref="ILlamaServerProcessSupervisor.EjectAsync" /> is asked first, per <see cref="ModelRole" /> and never forced: it costs no I/O when
    ///     nothing is running, and stopping the child process is both what frees its VRAM and how edited launch arguments take effect, since the next
    ///     request respawns it. The Ollama <c>keep_alive=0</c> eviction follows when that runtime is enabled
    ///     (<see cref="XE_Local_AI_Engine.Client.Services.NodeSettings.OllamaRuntimeGate.RuntimeEnabledConfigurationKey" />, the gate the running-models
    ///     endpoint reads); an <em>unreachable</em> daemon is not an error, a daemon answering with a failure status is.
    /// </remarks>
    /// <returns>
    ///     <see langword="false" /> only when a llama-server process was still busy when the bounded drain window elapsed and was left running.
    /// </returns>
    Task<bool> UnloadAsync(string modelName, CancellationToken cancellationToken = default);
}
