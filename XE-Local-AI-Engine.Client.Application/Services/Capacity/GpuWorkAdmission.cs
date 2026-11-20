namespace XE_Local_AI_Engine.Client.Services.Capacity;

using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>The one-retry admission for callers that refuse at once: an idle GPU holder is waited out once, busy work still refuses.</summary>
/// <remarks>
///     The gate's refusal already asked the idle holder to leave. With no holder registered the wait returns at once and
///     the single retry meets the same holder, so such a node refuses exactly as before.
/// </remarks>
public static class GpuWorkAdmission
{
    /// <summary>How long a refused caller waits for an idle GPU holder to release before it refuses as before.</summary>
    public static readonly TimeSpan IdleHolderReleaseBound = TimeSpan.FromSeconds(30);

    /// <summary>Takes the whole node, retrying once after an idle holder released; <see langword="null" /> when still refused.</summary>
    public static async Task<IDisposable?> BeginExclusiveOrNullAsync(this IGpuWorkGate gate,
        IGpuIdleHolderPreemption idlePreemption,
        GpuWorkKind kind,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(idlePreemption);
        if (gate.TryBeginExclusive(kind, out var handle))
        {
            return handle;
        }

        return await idlePreemption.WaitForReleaseAsync(IdleHolderReleaseBound, ct) && gate.TryBeginExclusive(kind, out handle) ? handle : null;
    }

    /// <summary>Takes a shared hold, retrying once after an idle holder released; <see langword="null" /> when still refused.</summary>
    public static async Task<IDisposable?> BeginSharedOrNullAsync(this IGpuWorkGate gate,
        IGpuIdleHolderPreemption idlePreemption,
        GpuWorkKind kind,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(idlePreemption);
        if (gate.TryBeginShared(kind, out var handle))
        {
            return handle;
        }

        return await idlePreemption.WaitForReleaseAsync(IdleHolderReleaseBound, ct) && gate.TryBeginShared(kind, out handle) ? handle : null;
    }
}
