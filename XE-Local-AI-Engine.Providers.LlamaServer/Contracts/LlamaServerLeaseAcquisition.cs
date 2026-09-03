namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The outcome of <see cref="ILlamaServerProcessSupervisor.TryAcquireInferenceLease" />, sampled atomically at
///     acquire time so the caller can distinguish WHY no lease was granted. Exactly one of four shapes: a granted
///     <see cref="Lease" /> (live, non-evicting process); refused with <see cref="ProcessEvicting" /> set (an operator
///     eject is draining the process — the caller must fail the request as operator-ejected rather than run it
///     untracked under the drain, where the teardown would kill it mid-flight); refused with
///     <see cref="ProcessProfiling" /> set (a measurement spawn owns the key — the caller must re-ensure, see that
///     member); or refused with none of them (no live process backs the key — the caller proceeds leaseless and relies
///     on ensure/self-heal).
/// </summary>
public readonly record struct LlamaServerLeaseAcquisition(
    ILlamaServerInferenceLease? Lease,
    bool ProcessEvicting,
    bool ProcessProfiling = false)
{
    /// <summary>Refused because no live process backs the key (absent or already exited).</summary>
    public static LlamaServerLeaseAcquisition NotRunning { get; } = new(Lease: null, ProcessEvicting: false);

    /// <summary>Refused because an operator eject is draining the process — no new inference may start against it.</summary>
    public static LlamaServerLeaseAcquisition Evicting { get; } = new(Lease: null, ProcessEvicting: true);

    /// <summary>
    ///     Refused because a profiling/benchmark spawn owns this key right now.
    ///     <para>
    ///         Deliberately NOT reported as <see cref="NotRunning" />: callers resolve the endpoint BEFORE they take
    ///         the lease, and the port allocator commonly re-uses the port the replaced process just freed. A caller
    ///         that treated this as "absent" and proceeded leaseless — which is what "absent" licenses — would send
    ///         its request to the measurement process on a cached endpoint, contaminating the measurement and then
    ///         being killed by profiling's teardown.
    ///     </para>
    ///     <para>
    ///         The caller must instead re-ensure and try again: it is transient and self-clearing, because profiling
    ///         holds the per-key single-flight gate through its own teardown, so the next
    ///         <see cref="ILlamaServerProcessSupervisor.EnsureRunningAsync" /> parks on that gate and then returns a
    ///         process of the caller's own. Callers bound the retry so back-to-back measurements surface as a
    ///         retryable "busy" rather than an unbounded wait.
    ///     </para>
    ///     <para>
    ///         Accepted trade-off: that re-ensure can wait out a whole benchmark body while holding the shared
    ///         runtime-mutation gate, so a warm request may block for the length of one measurement — bounded by the
    ///         benchmark, and preferred over sending the request into the measurement process.
    ///     </para>
    /// </summary>
    public static LlamaServerLeaseAcquisition ProfilingOwned { get; } = new(Lease: null, ProcessEvicting: false, ProcessProfiling: true);

    /// <summary>A granted lease over a live, non-evicting process. The caller MUST dispose it when the request ends.</summary>
    public static LlamaServerLeaseAcquisition Granted(ILlamaServerInferenceLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return new LlamaServerLeaseAcquisition(lease, ProcessEvicting: false);
    }
}
