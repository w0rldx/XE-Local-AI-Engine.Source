namespace XE_Local_AI_Engine.Client.Services.Capacity;

using XE_Local_AI_Engine.Providers.LlamaServer;

/// <summary>Serializes same-model sub-agent runs against the one running <c>(model, role)</c> process.</summary>
/// <remarks>
///     A <see cref="CapacityVerdict.QueueSameModel" /> spawn loads no second copy: it queues behind any other same-model spawn, because
///     <c>BuildLaunchSpec</c> passes neither <c>--parallel</c> nor <c>-np</c>, so the llama-server runs with ~1 slot. The wait is BOUNDED — a
///     spawn that cannot take the model's turn within the timeout is rejected ("busy") rather than hanging the parent tool call. A singleton,
///     so the per-<c>(model, role)</c> semaphore map is shared by every concurrent spawn; the per-turn fan-out and cloud counters live on
///     <see cref="SpawnContext" />.
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
