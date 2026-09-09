namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Gracefully evicts a model from the local runtimes' memory, wherever it is resident. BOTH runtimes are asked,
///     because the node cannot know which one holds the model: residency is a property of a running process, and the
///     per-model provider map only records where a model would be <em>served</em>. A model pulled into Ollama has no map
///     row at all, so routing this action by that map ejected nothing and reported success while Ollama still held the
///     weights.
///     <list type="bullet">
///         <item>
///             <description>
///                 First <see cref="ILlamaServerProcessSupervisor.EjectAsync" /> per <see cref="ModelRole" />, never
///                 forced. This costs no I/O when nothing is running, and stopping the child process is both what frees
///                 its VRAM and how edited launch arguments take effect: the next request respawns it.
///             </description>
///         </item>
///         <item>
///             <description>
///                 Then the Ollama <c>keep_alive=0</c> eviction, when the optional Ollama runtime is enabled
///                 (<see cref="XE_Local_AI_Engine.Client.Services.NodeSettings.OllamaRuntimeGate.RuntimeEnabledConfigurationKey" />,
///                 the same gate the running-models endpoint reads). An <em>unreachable</em> daemon means nothing
///                 is resident there, which is not an error; a daemon that answers with a failure status still is.
///             </description>
///         </item>
///     </list>
///     Both paths let an in-flight generation complete before the model is evicted, so unload never interrupts a running
///     turn.
/// </summary>
public interface IModelUnloadCoordinator
{
    /// <summary>
    ///     Unloads <paramref name="modelName" /> from every local runtime that may hold it. Idempotent: unloading a
    ///     model that is not loaded still reports success. Returns <see langword="false" /> only when a llama-server
    ///     process was still busy when the bounded drain window elapsed and was therefore left running.
    /// </summary>
    Task<bool> UnloadAsync(string modelName, CancellationToken cancellationToken = default);
}
