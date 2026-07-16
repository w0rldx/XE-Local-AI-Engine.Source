namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Owns the lifecycle of every <c>llama-server</c> child process: reuse-or-spawn per <c>(model, role)</c>, health
///     aggregation, idle-TTL + loaded-cap eviction + reaper (shared eviction policy), restart-backoff, port
///     allocation, and tree-kill teardown. All processes are same-user, unprivileged, localhost-bound.
/// </summary>
/// <remarks>
///     <para>
///         <strong>This is the contract only; the implementation supplies the body.</strong> Implementations are
///         singleton — the supervisor owns all processes and disposes them on shutdown. Every async member flows a
///         <see cref="CancellationToken" />.
///     </para>
///     <para>
///         Mandatory launch flags: a chat process is launched with <c>--jinja</c>; an embedding process is launched
///         with a non-<c>none</c> pooling type. Each distinct <c>(model, role)</c> is a distinct process and counts
///         against the loaded-cap.
///     </para>
/// </remarks>
public interface ILlamaServerProcessSupervisor
{
    /// <summary>
    ///     Reuses the running <c>(model, role)</c> process or spawns one (single-flight per key), then returns its
    ///     localhost OpenAI-compatible endpoint. Spawning a new distinct model when the loaded-cap is full rejects.
    /// </summary>
    /// <exception cref="LlamaRuntimeException">
    ///     Spawn failed, the loaded-cap was reached, or the restart-backoff cap was exceeded — message is sanitized.
    /// </exception>
    Task<LlamaServerEndpoint> EnsureRunningAsync(string modelName, ModelRole role, CancellationToken ct);

    /// <summary>Evicts (tree-kills) the <c>(model, role)</c> process if running and releases its port. Idempotent.</summary>
    /// <remarks>
    ///     This is the <strong>immediate</strong> teardown used internally (idle-reaper simulation in tests, profiling
    ///     exclusivity, provider unload): it does not wait for in-flight inference to finish. For an
    ///     <strong>operator</strong> eject that must not interrupt a running turn, use <see cref="EjectAsync" />.
    /// </remarks>
    Task EvictAsync(string modelName, ModelRole role, CancellationToken ct);

    /// <summary>
    ///     Operator eject for a supervised <c>(model, role)</c> process. Marks the process evicting (no new leases), then
    ///     waits up to the configured bounded drain window for in-flight inference (tracked via
    ///     <see cref="TryAcquireInferenceLease" />) to finish before tearing it down. An idle process (no active leases)
    ///     is torn down immediately. When the drain window elapses with work still in flight, the process is left running
    ///     and the outcome reports it could not complete safely — unless <paramref name="force" /> is set, in which case
    ///     the process is torn down anyway and the interrupted run is marked as operator-ejected (not a generic failure).
    /// </summary>
    /// <param name="modelName">Model whose process to eject.</param>
    /// <param name="role">Role of the process to eject (chat / embedding / reranker).</param>
    /// <param name="force">When set, tear the process down even if in-flight work has not drained.</param>
    /// <param name="ct">Cancellation for the (bounded) drain wait.</param>
    /// <returns>The eject outcome (idempotent no-op when nothing is running).</returns>
    Task<LlamaServerEjectOutcome> EjectAsync(string modelName, ModelRole role, bool force, CancellationToken ct);

    /// <summary>
    ///     Acquires a reference-counted inference lease against the currently-running <c>(model, role)</c> process so a
    ///     graceful <see cref="EjectAsync" /> waits for the request to finish before teardown. Returns <c>null</c> when
    ///     no live, non-evicting process backs the key (the caller then proceeds without a lease — the deferred client
    ///     self-heals on the resulting connection failure). The caller MUST dispose the returned lease when the request
    ///     completes (success, failure, or cancellation).
    /// </summary>
    ILlamaServerInferenceLease? TryAcquireInferenceLease(string modelName, ModelRole role);

    /// <summary>
    ///     The operator profiling entry point (explore + benchmark). Acquires the SAME single-flight gate the normal
    ///     ensure-running path uses for this <c>(model, role)</c> — so concurrent user <see cref="EnsureRunningAsync" />
    ///     calls for the key queue behind it — then evicts any warm process for the key and spawns exactly ONE
    ///     <c>llama-server</c> with <paramref name="launchArgs" /> applied verbatim (bypassing the profile resolver:
    ///     explore passes <see cref="ResolvedLaunchArguments.Explore" />, benchmark passes the drafted
    ///     <see cref="ResolvedLaunchArguments.Replay" />). When <paramref name="enableMetrics" /> is set and the built
    ///     args do not already carry <c>--metrics</c>, it is appended so the benchmark can read <c>/metrics</c>. The
    ///     spawned process is pinned against idle eviction for the duration of <paramref name="body" />; on completion,
    ///     throw, or cancellation the pin is dropped and the transient profiling process is evicted (tree-killed) and
    ///     the gate released.
    /// </summary>
    /// <typeparam name="T">The result the profiling body produces (for example a captured benchmark measurement).</typeparam>
    /// <param name="modelName">Model to profile.</param>
    /// <param name="role">Role to profile (chat vs embedding).</param>
    /// <param name="launchArgs">The exact launch arguments to spawn with (explore or a drafted replay profile).</param>
    /// <param name="enableMetrics">Append <c>--metrics</c> when the built args do not already include it.</param>
    /// <param name="body">The profiling work to run against the exclusive process and its captured startup output.</param>
    /// <param name="ct">Cancellation token flowed through spawn, body, and teardown.</param>
    Task<T> RunExclusiveProfilingAsync<T>(string modelName,
        ModelRole role,
        ResolvedLaunchArguments launchArgs,
        bool enableMetrics,
        Func<LlamaServerProfilingContext, CancellationToken, Task<T>> body,
        CancellationToken ct);

    /// <summary>
    ///     Aggregates every running process's health into one snapshot — operational iff the supervisor can serve
    ///     requests; per-process detail is surfaced for diagnostics. This performs a live responsiveness probe per
    ///     process, so it is for the diagnostics surface — NOT a hot path. For a cheap running-count read use
    ///     <see cref="CountRunningProcesses" />.
    /// </summary>
    Task<IReadOnlyList<LlamaServerProcessHealth>> CheckHealthAsync(CancellationToken ct);

    /// <summary>
    ///     The number of currently-running <c>(model, role)</c> processes the supervisor owns, counting only handles
    ///     that have not exited. This is a synchronous in-memory read of the process table — NO health/HTTP probe — so it
    ///     is safe on hot paths (runtime-status GET, the pre-update safety gate). Ollama is an external provider the
    ///     supervisor does not own, so it is never counted.
    /// </summary>
    int CountRunningProcesses();

    /// <summary>
    ///     Live runtime facts for the running <c>(model, role)</c> process — currently the effective context window it
    ///     loaded (from <c>/props</c>, captured after readiness). Returns <see langword="null" /> when the process is not
    ///     running, has exited, or its effective context could not be read. Synchronous in-memory read — no HTTP.
    /// </summary>
    LlamaServerRuntimeInfo? GetRuntimeInfo(string modelName, ModelRole role);
}
