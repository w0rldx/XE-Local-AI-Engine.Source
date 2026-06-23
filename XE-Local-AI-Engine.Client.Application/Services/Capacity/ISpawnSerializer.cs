namespace XE_Local_AI_Engine.Client.Services.Capacity;

using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>
///     Serializes same-model sub-agent runs against the one running <c>(model, role)</c> process. A
///     <see cref="CapacityVerdict.QueueSameModel" /> spawn does not load a second copy of an already-running model;
///     instead it queues behind any other same-model spawn so concurrent requests do not pile onto a llama-server
///     launched with ~1 slot (<c>BuildLaunchSpec</c> passes neither <c>--parallel</c> nor <c>-np</c>). The wait is
///     BOUNDED — a queued spawn that cannot acquire the model's turn within the timeout is rejected ("busy") rather than
///     hanging the parent tool call.
/// </summary>
/// <remarks>
///     Process-wide singleton: the per-<c>(model, role)</c> semaphore map must be shared by every concurrent spawn on
///     the node so two same-model spawns observe the same gate. The per-turn fan-out and cloud counters live on
///     <see cref="SpawnContext" />, not here.
/// </remarks>
public interface ISpawnSerializer
{
    /// <summary>
    ///     Runs <paramref name="run" /> while holding the serialization turn for <paramref name="modelName" /> /
    ///     <paramref name="role" />. Acquires the model's semaphore with a bounded wait; on timeout invokes
    ///     <paramref name="onTimeout" /> and returns its value WITHOUT running <paramref name="run" />. Releases the turn
    ///     in a <c>finally</c>. Flows <paramref name="ct" /> to both the wait and the run.
    /// </summary>
    Task<string> RunSerializedAsync(string modelName,
        ModelRole role,
        TimeSpan timeout,
        Func<CancellationToken, Task<string>> run,
        Func<string> onTimeout,
        CancellationToken ct);
}
