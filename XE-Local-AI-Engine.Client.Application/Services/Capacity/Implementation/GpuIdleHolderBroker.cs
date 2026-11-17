namespace XE_Local_AI_Engine.Client.Services.Capacity.Implementation;

using XE_Local_AI_Engine.Providers.Abstractions.Capabilities;

/// <summary>The node's <see cref="IGpuIdleHolderPreemption" />: forwards every GPU demand to the registered <see cref="IGpuIdleHolder" />s.</summary>
/// <remarks>
///     A singleton registry. Registration swaps an immutable snapshot under a lock; every holder call runs on a snapshot
///     outside it, so a holder's own lock never nests inside this one. With nothing registered each member is a no-op.
/// </remarks>
public sealed class GpuIdleHolderBroker : IGpuIdleHolderPreemption
{
    private readonly Lock _sync = new();
    private IGpuIdleHolder[] _holders = [];

    /// <inheritdoc />
    public bool IsIdlePreemptible
    {
        get
        {
            var holders = Volatile.Read(ref _holders);
            return holders.Length > 0 && Array.TrueForAll(holders, static holder => holder.IsIdle);
        }
    }

    /// <summary>Adds <paramref name="holder" />; registering it twice is a no-op.</summary>
    public void Register(IGpuIdleHolder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        lock (_sync)
        {
            if (!_holders.Contains(holder))
            {
                Volatile.Write(ref _holders, [.. _holders, holder]);
            }
        }
    }

    /// <summary>Removes <paramref name="holder" />; an unknown holder is a no-op.</summary>
    public void Deregister(IGpuIdleHolder holder)
    {
        ArgumentNullException.ThrowIfNull(holder);
        lock (_sync)
        {
            Volatile.Write(ref _holders, [.. _holders.Where(registered => !ReferenceEquals(registered, holder))]);
        }
    }

    /// <inheritdoc />
    public void RequestPreemption()
    {
        foreach (var holder in Volatile.Read(ref _holders))
        {
            holder.RequestPreemption();
        }
    }

    /// <inheritdoc />
    public async Task<bool> WaitForReleaseAsync(TimeSpan bound, CancellationToken ct)
    {
        var holders = Volatile.Read(ref _holders);
        if (holders.Length == 0)
        {
            return true;
        }

        foreach (var holder in holders)
        {
            holder.RequestPreemption();
        }

        var released = await Task.WhenAll(holders.Select(holder => holder.WaitForReleaseAsync(bound, ct)));
        return Array.TrueForAll(released, static isReleased => isReleased);
    }
}
