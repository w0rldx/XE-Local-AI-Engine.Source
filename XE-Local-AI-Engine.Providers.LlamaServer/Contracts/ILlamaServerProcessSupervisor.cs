namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Owns the lifecycle of every <c>llama-server</c> child process: reuse-or-spawn per <c>(model, role)</c>, health
///     aggregation, eviction and reaping, restart-backoff, port allocation and tree-kill teardown.
/// </summary>
/// <remarks>
///     All processes are same-user, unprivileged and localhost-bound. An implementation is a singleton — the supervisor
///     owns every process and disposes them on shutdown — and every async member flows a
///     <see cref="CancellationToken" />. Mandatory launch flags: a chat process carries <c>--jinja</c> and an embedding
///     process a non-<c>none</c> pooling type. Each distinct <c>(model, role)</c> is a distinct process and counts
///     against the loaded-cap.
/// </remarks>
public interface ILlamaServerProcessSupervisor
{
    /// <summary>
    ///     Atomically acquires an exclusive runtime-mutation lease only when no process is running or starting. While
    ///     held, new <see cref="EnsureRunningAsync" /> calls wait. Returns null when a process already owns the runtime.
    /// </summary>
    Task<ILlamaServerRuntimeMutationLease?> TryAcquireRuntimeMutationLeaseAsync(CancellationToken ct)
    {
        return Task.FromResult<ILlamaServerRuntimeMutationLease?>(null);
    }

    /// <summary>
    ///     Whether automatic keep-warm starts must yield to a pending or in-flight runtime mutation. Interactive model
    ///     requests remain governed by <see cref="EnsureRunningAsync" /> and are not suppressed by this signal.
    /// </summary>
    bool IsKeepWarmSuppressed()
    {
        return false;
    }

    /// <summary>
    ///     Reuses the running <c>(model, role)</c> process or spawns one (single-flight per key), then returns its
    ///     localhost OpenAI-compatible endpoint. Spawning a new distinct model when the loaded-cap is full rejects.
    /// </summary>
    /// <exception cref="LlamaRuntimeException">
    ///     Spawn failed, the loaded-cap was reached, or the restart-backoff cap was exceeded — message is sanitized.
    /// </exception>
    Task<LlamaServerEndpoint> EnsureRunningAsync(string modelName, ModelRole role, CancellationToken ct);

    /// <summary>
    ///     Waits up to <paramref name="timeout" /> until the <c>(model, role)</c> process is observed exited. True when it
    ///     exited or none is registered, so the next <see cref="EnsureRunningAsync" /> respawns instead of reusing it.
    /// </summary>
    /// <remarks>
    ///     A caller whose socket to the process was refused calls this before re-ensuring: the kernel closes a killed
    ///     process's sockets before its parent is notified, so an immediate ensure can still hand back the dead endpoint.
    /// </remarks>
    Task<bool> WaitForProcessExitAsync(string modelName, ModelRole role, TimeSpan timeout, CancellationToken ct)
    {
        return Task.FromResult(false);
    }

    /// <summary>Evicts (tree-kills) the <c>(model, role)</c> process if running and releases its port. Idempotent.</summary>
    /// <remarks>
    ///     This is the <strong>immediate</strong> teardown used internally (idle-reaper simulation in tests, profiling
    ///     exclusivity, provider unload): it does not wait for in-flight inference to finish. For an
    ///     <strong>operator</strong> eject that must not interrupt a running turn, use <see cref="EjectAsync" />.
    /// </remarks>
    Task EvictAsync(string modelName, ModelRole role, CancellationToken ct);

    /// <summary>
    ///     Evicts every role-specific process for <paramref name="modelName" />. The role set is derived from
    ///     <see cref="Enum.GetValues{TEnum}" /> so a future <see cref="ModelRole" /> member is included automatically
    ///     instead of silently leaving one process resident. Idempotent when any or all roles are not running.
    /// </summary>
    Task EvictAllRolesAsync(string modelName, CancellationToken ct)
    {
        return EvictAllRolesCoreAsync(this, modelName, ct);

        static async Task EvictAllRolesCoreAsync(ILlamaServerProcessSupervisor supervisor,
            string name,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            foreach (var role in Enum.GetValues<ModelRole>())
            {
                await supervisor.EvictAsync(name, role, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    ///     Operator eject for a supervised <c>(model, role)</c> process: it marks the process evicting, taking no new
    ///     leases, then waits out the bounded drain window before tearing it down.
    /// </summary>
    /// <remarks>
    ///     In-flight inference is tracked through <see cref="TryAcquireInferenceLease" />, and an idle process is torn
    ///     down immediately. When the window elapses with work still in flight the process is left RUNNING and the
    ///     outcome reports that it could not complete safely — unless <paramref name="force" /> is set, in which case
    ///     it is torn down anyway and the interrupted run is marked operator-ejected rather than a generic failure.
    /// </remarks>
    /// <param name="modelName">Model whose process to eject.</param>
    /// <param name="role">Role of the process to eject (chat / embedding / reranker).</param>
    /// <param name="force">When set, tear the process down even if in-flight work has not drained.</param>
    /// <param name="ct">Cancellation for the (bounded) drain wait.</param>
    /// <returns>The eject outcome (idempotent no-op when nothing is running).</returns>
    Task<LlamaServerEjectOutcome> EjectAsync(string modelName, ModelRole role, bool force, CancellationToken ct);

    /// <summary>
    ///     Acquires a reference-counted inference lease against the running <c>(model, role)</c> process, so a graceful
    ///     <see cref="EjectAsync" /> waits for the request to finish before teardown.
    /// </summary>
    /// <remarks>
    ///     The result distinguishes the outcomes atomically, sampled at acquire time: a granted lease, which the caller
    ///     MUST dispose on completion, failure or cancellation; <see cref="LlamaServerLeaseAcquisition.ProcessEvicting" />,
    ///     where the caller must fail the request as operator-ejected rather than run it untracked under the drain, to
    ///     be killed mid-flight and self-heal-respawned; or no live process behind the key, where the caller proceeds
    ///     leaseless and the deferred client self-heals on the resulting connection failure.
    /// </remarks>
    LlamaServerLeaseAcquisition TryAcquireInferenceLease(string modelName, ModelRole role);

    /// <summary>The operator profiling entry point, explore and benchmark: it evicts any warm process for the key, then spawns exactly ONE process.</summary>
    /// <remarks>
    ///     It bypasses the profile resolver and takes the SAME single-flight gate the ensure-running path uses, so
    ///     concurrent <see cref="EnsureRunningAsync" /> calls for this <c>(model, role)</c> queue behind it, and the
    ///     token flows through spawn, body and teardown. Explore applies the production launch policy to
    ///     <see cref="ResolvedLaunchArguments.Explore" /> so fitted evidence reflects normal serving, benchmark the
    ///     drafted <see cref="ResolvedLaunchArguments.Replay" /> verbatim. On return, throw or cancellation alike the spawn is tree-killed and the gate released.
    /// </remarks>
    /// <typeparam name="T">What the profiling body produces, for example a captured benchmark measurement.</typeparam>
    /// <param name="launchArgs">Explore mode or the exact drafted replay arguments to spawn with.</param>
    /// <param name="enableMetrics">Append <c>--metrics</c> when the built args do not already include it.</param>
    /// <param name="body">The profiling work run against the exclusive process and its captured startup output, which is pinned against idle eviction for its duration.</param>
    /// <param name="captureVramBeforeSpawn">
    ///     Optional ambient-VRAM capture run after same-key eviction and before the spawn, propagated as <see cref="LlamaServerProfilingContext.PreSpawnVram" />.
    /// </param>
    Task<T> RunExclusiveProfilingAsync<T>(string modelName,
        ModelRole role,
        ResolvedLaunchArguments launchArgs,
        bool enableMetrics,
        Func<LlamaServerProfilingContext, CancellationToken, Task<T>> body,
        CancellationToken ct,
        Func<CancellationToken, Task<LlamaServerProfilingVramSnapshot>>? captureVramBeforeSpawn = null);

    /// <summary>
    ///     Benchmark-only exact spawn. Unlike ordinary chat and operator profiling, this consumes an explicit frozen
    ///     launch policy and cannot inherit mutable cache or speculative-decoding settings.
    /// </summary>
    Task<T> RunExclusiveBenchmarkAsync<T>(string modelName,
        ModelRole role,
        ResolvedLaunchArguments launchArgs,
        LlamaServerBenchmarkLaunchPolicy launchPolicy,
        Func<LlamaServerProfilingContext, CancellationToken, Task<T>> body,
        CancellationToken ct)
    {
        throw new NotSupportedException("This supervisor does not support exact benchmark launches.");
    }

    /// <summary>
    ///     Aggregates every running process's health into one snapshot — operational iff the supervisor can serve
    ///     requests — with per-process detail surfaced for diagnostics.
    /// </summary>
    /// <remarks>
    ///     It performs a live responsiveness probe per process, so it belongs on the diagnostics surface and NOT on a
    ///     hot path; for a cheap running-count read use <see cref="CountRunningProcesses" />.
    /// </remarks>
    Task<IReadOnlyList<LlamaServerProcessHealth>> CheckHealthAsync(CancellationToken ct);

    /// <summary>
    ///     The number of currently-running <c>(model, role)</c> processes the supervisor owns, counting only handles
    ///     that have not exited.
    /// </summary>
    /// <remarks>
    ///     A synchronous in-memory read of the process table with NO health or HTTP probe, so it is safe on hot paths
    ///     such as the runtime-status GET and the pre-update safety gate. Ollama is an external provider the supervisor
    ///     does not own, so it is never counted.
    /// </remarks>
    int CountRunningProcesses();

    /// <summary>
    ///     Live runtime facts for the running <c>(model, role)</c> process — currently the effective context window it
    ///     loaded, read from <c>/props</c> after readiness. A synchronous in-memory read with no HTTP.
    /// </summary>
    /// <remarks>
    ///     <see langword="null" /> when the process is not running, has exited, or its effective context could not be
    ///     read.
    /// </remarks>
    LlamaServerRuntimeInfo? GetRuntimeInfo(string modelName, ModelRole role);
}
