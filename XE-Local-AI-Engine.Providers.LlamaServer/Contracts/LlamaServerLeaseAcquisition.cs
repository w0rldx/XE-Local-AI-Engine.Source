namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     The outcome of <see cref="ILlamaServerProcessSupervisor.TryAcquireInferenceLease" />, sampled atomically at
///     acquire time so the caller can distinguish WHY no lease was granted. Exactly one of three shapes: a granted
///     <see cref="Lease" /> (live, non-evicting process); refused with <see cref="ProcessEvicting" /> set (an operator
///     eject is draining the process — the caller must fail the request as operator-ejected rather than run it
///     untracked under the drain, where the teardown would kill it mid-flight); or refused with neither (no live
///     process backs the key — the caller proceeds leaseless and relies on ensure/self-heal).
/// </summary>
public readonly record struct LlamaServerLeaseAcquisition(ILlamaServerInferenceLease? Lease, bool ProcessEvicting)
{
    /// <summary>Refused because no live process backs the key (absent or already exited).</summary>
    public static LlamaServerLeaseAcquisition NotRunning { get; } = new(Lease: null, ProcessEvicting: false);

    /// <summary>Refused because an operator eject is draining the process — no new inference may start against it.</summary>
    public static LlamaServerLeaseAcquisition Evicting { get; } = new(Lease: null, ProcessEvicting: true);

    /// <summary>A granted lease over a live, non-evicting process. The caller MUST dispose it when the request ends.</summary>
    public static LlamaServerLeaseAcquisition Granted(ILlamaServerInferenceLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return new LlamaServerLeaseAcquisition(lease, ProcessEvicting: false);
    }
}
