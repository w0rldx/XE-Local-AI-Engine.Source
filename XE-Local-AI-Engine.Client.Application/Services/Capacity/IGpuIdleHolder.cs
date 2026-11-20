namespace XE_Local_AI_Engine.Client.Services.Capacity;

/// <summary>A GPU holder that may sit idle on the node's GPU and gives it back on demand; registered with <see cref="Implementation.GpuIdleHolderBroker" />.</summary>
/// <remarks>
///     States: admitting, active, idle, evicting. Register while admitting, BEFORE taking the GPU work gate, so demand that
///     races the admission is latched; deregister when the admission fails or the eviction completes. The broker calls
///     every member outside its own lock, and a holder must not call back into the broker under its lock.
/// </remarks>
public interface IGpuIdleHolder
{
    /// <summary>True only while idle: holding the GPU with no job running.</summary>
    bool IsIdle { get; }

    /// <summary>Records demand: starts the holder-owned eviction when idle, latches it while admitting or active. Never awaits, never throws.</summary>
    /// <remarks>The eviction must not use any requester's cancellation token, and starts at most once per holder.</remarks>
    void RequestPreemption();

    /// <summary>Waits up to <paramref name="bound" /> for the holder to release the GPU; never calls the broker. While admitting or active it latches the pending preemption and returns false at once.</summary>
    /// <remarks>
    ///     <see langword="true" /> once released (idle or evicting, then gone). <see langword="false" /> at once while
    ///     admitting or active, and when the bound elapses. Cancelling <paramref name="ct" /> ends only this wait.
    /// </remarks>
    Task<bool> WaitForReleaseAsync(TimeSpan bound, CancellationToken ct);
}
