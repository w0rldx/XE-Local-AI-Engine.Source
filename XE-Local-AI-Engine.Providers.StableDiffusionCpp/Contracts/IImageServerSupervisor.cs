namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>
///     Owns the resident <c>sd-server</c> daemon(s): reuse-or-spawn one process per model, readiness-gate it, and
///     tree-kill + restart it on demand. Mirrors <c>ILlamaServerProcessSupervisor</c> for the image runtime; consumed
///     only by <see cref="Implementation.StableDiffusionCppRuntime" />.
/// </summary>
internal interface IImageServerSupervisor
{
    /// <summary>
    ///     Ensures a ready <c>sd-server</c> daemon serving <paramref name="modelName" /> is running and returns its
    ///     loopback endpoint. Reuses a live daemon; otherwise resolves the model file-set, selects the GPU backend,
    ///     acquires the binary, launches, and waits for readiness (poll <c>/sdcpp/v1/capabilities</c>).
    /// </summary>
    /// <exception cref="StableDiffusionRuntimeException">The model is not installed, or the daemon failed to start/become ready.</exception>
    Task<ImageServerEndpoint> EnsureRunningAsync(string modelName, CancellationToken ct);

    /// <summary>
    ///     Tree-kills the daemon serving <paramref name="modelName" /> and spawns a fresh one, returning its endpoint.
    ///     This is the abort path for a job that is already generating (sd-server cannot interrupt an in-flight
    ///     generation over HTTP, §4A) — killing the daemon drops the one active job and the restart readies the slot.
    /// </summary>
    Task<ImageServerEndpoint> RestartAsync(string modelName, CancellationToken ct);

    /// <summary>Tree-kills and forgets the daemon serving <paramref name="modelName" />, if any. Idempotent.</summary>
    Task EvictAsync(string modelName, CancellationToken ct);

    /// <summary>
    ///     Acquires an active-job lease against the resident daemon serving <paramref name="modelName" /> so the idle
    ///     reaper and cap-admission LRU evictor keep it alive while a generation is in flight. Returns
    ///     <see langword="null" /> when no live daemon backs the model — the caller then proceeds leaseless and relies on
    ///     the just-completed <see cref="EnsureRunningAsync" /> plus self-heal. Call immediately after
    ///     <see cref="EnsureRunningAsync" /> and dispose the lease when the job ends (completed / failed / cancelled).
    /// </summary>
    IImageServerJobLease? TryAcquireJobLease(string modelName);
}
