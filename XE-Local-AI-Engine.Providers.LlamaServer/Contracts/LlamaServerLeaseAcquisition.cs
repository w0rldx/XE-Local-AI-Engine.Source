namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The outcome of <see cref="ILlamaServerProcessSupervisor.TryAcquireInferenceLease" />, sampled atomically at
///     acquire time so the caller can distinguish WHY no lease was granted.
/// </summary>
/// <remarks>
///     Exactly one of four shapes: a granted <see cref="Lease" /> against a live, non-evicting process; refused with
///     <see cref="ProcessEvicting" />, where an operator eject is draining and the caller must fail the request as
///     operator-ejected rather than run it untracked under the drain and be killed mid-flight; refused with
///     <see cref="ProcessProfiling" />, where a measurement spawn owns the key and the caller must re-ensure; or
///     refused with none of them, where no live process backs the key and the caller proceeds leaseless.
/// </remarks>
public readonly record struct LlamaServerLeaseAcquisition(
    ILlamaServerInferenceLease? Lease,
    bool ProcessEvicting,
    bool ProcessProfiling = false)
{
    /// <summary>Refused because no live process backs the key (absent or already exited).</summary>
    public static LlamaServerLeaseAcquisition NotRunning { get; } = new(Lease: null, ProcessEvicting: false);

    /// <summary>Refused because an operator eject is draining the process — no new inference may start against it.</summary>
    public static LlamaServerLeaseAcquisition Evicting { get; } = new(Lease: null, ProcessEvicting: true);

    /// <summary>Refused because a profiling or benchmark spawn owns this key right now.</summary>
    /// <remarks>
    ///     Deliberately NOT <see cref="NotRunning" />: callers resolve the endpoint BEFORE taking the lease and the
    ///     port allocator commonly re-uses the port the replaced process just freed, so a caller treating this as
    ///     "absent" and proceeding leaseless would send its request into the measurement and then be killed by
    ///     profiling's teardown. It must re-ensure and retry, bounded, so back-to-back measurements surface as a
    ///     retryable busy. See docs/wiki/03-local-runtime-and-providers.md, "Deferred chat / embedding clients".
    /// </remarks>
    public static LlamaServerLeaseAcquisition ProfilingOwned { get; } = new(Lease: null, ProcessEvicting: false, ProcessProfiling: true);

    /// <summary>A granted lease over a live, non-evicting process. The caller MUST dispose it when the request ends.</summary>
    public static LlamaServerLeaseAcquisition Granted(ILlamaServerInferenceLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return new LlamaServerLeaseAcquisition(lease, ProcessEvicting: false);
    }
}
