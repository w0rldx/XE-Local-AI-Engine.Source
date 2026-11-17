namespace XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>Lets any GPU demand ask an idle GPU holder, such as a warm Model Lab worker, to give the GPU back.</summary>
/// <remarks>
///     With no registered holder every member is a no-op, so a node that never warms a holder behaves as before. A busy
///     (admitting or active) holder is never preempted: the demand is latched and served when its job ends. No member
///     calls a holder under a lock, and no requester's token ever reaches an eviction.
/// </remarks>
public interface IGpuIdleHolderPreemption
{
    /// <summary>True only while a holder is registered and every registered holder is idle, so a reader may count it as not busy.</summary>
    bool IsIdlePreemptible { get; }

    /// <summary>Records GPU demand: evicts an idle holder at once and latches the demand on a busy one. Never awaits, never throws.</summary>
    void RequestPreemption();

    /// <summary>Raises demand as <see cref="RequestPreemption" /> does, then waits up to <paramref name="bound" /> for the holder to release the GPU.</summary>
    /// <remarks>
    ///     <see langword="true" /> at once with no holder, and once every holder has released. <see langword="false" /> at
    ///     once while a holder is admitting or active (the demand stays latched), and when the bound elapses. Cancelling
    ///     <paramref name="ct" /> ends only this wait, never the eviction it started.
    /// </remarks>
    Task<bool> WaitForReleaseAsync(TimeSpan bound, CancellationToken ct);
}
