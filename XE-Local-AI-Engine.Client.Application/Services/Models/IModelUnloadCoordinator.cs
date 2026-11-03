namespace XE_Local_AI_Engine.Client.Services.Models;

using XE_Local_AI_Engine.Providers.LlamaServer;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Gracefully evicts a model from llama-server's memory, in every role that holds it.</summary>
/// <remarks>
///     An in-flight generation completes before the model is evicted, so unload never interrupts a running turn.
/// </remarks>
public interface IModelUnloadCoordinator
{
    /// <summary>
    ///     Unloads <paramref name="modelName" />. Idempotent: unloading a model that is not loaded still reports success.
    /// </summary>
    /// <remarks>
    ///     <see cref="ILlamaServerProcessSupervisor.EjectAsync" /> is asked per <see cref="ModelRole" /> and never forced: it costs no I/O when
    ///     nothing is running, and stopping the child process is both what frees its VRAM and how edited launch arguments take effect, since the next
    ///     request respawns it.
    /// </remarks>
    /// <returns>
    ///     <see langword="false" /> only when a llama-server process was still busy when the bounded drain window elapsed and was left running.
    /// </returns>
    Task<bool> UnloadAsync(string modelName, CancellationToken cancellationToken = default);
}
