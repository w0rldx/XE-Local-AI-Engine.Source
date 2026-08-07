namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Resolves and caches the single process-context allocation shared by footprint and launch.</summary>
public interface IProcessContextAllocationResolver
{
    Task<ProcessContextAllocation?> ResolveAsync(string modelName,
        ModelRole role,
        GpuVariant variant,
        ResolvedLaunchArguments resolved,
        CancellationToken ct);

    /// <summary>
    /// Returns the next smaller hardware-tier allocation candidate for live admission sizing. Frozen and deterministic
    /// allocations are immutable. Candidate generation is pure and independent of classified-startup-OOM retry accounting.
    /// </summary>
    bool TryDownTierForAdmission(ProcessContextAllocation current, out ProcessContextAllocation downTiered);

    /// <summary>
    /// Atomically commits a fitting admission candidate without replacing an already-committed lower allocation. Returns
    /// the effective committed allocation so admission can reserve exactly what launch will consume.
    /// </summary>
    bool TryCommitAdmissionAllocation(ProcessContextAllocation candidate, out ProcessContextAllocation committed);

    /// <summary>
    /// Returns the effective committed allocation for the same admitted cache/content identity. Implementations must
    /// never return a larger context than <paramref name="admitted" />; immutable allocations pass through unchanged.
    /// </summary>
    bool TryGetEffectiveCommittedAllocation(ProcessContextAllocation admitted, out ProcessContextAllocation effective)
    {
        effective = admitted;
        return true;
    }

    /// <summary>
    /// Returns the next smaller automatic hardware tier after a classified startup OOM. Frozen and deterministic
    /// override allocations are never changed. At most two lower tiers are issued for one cache identity.
    /// </summary>
    bool TryDownTierAfterOutOfMemory(ProcessContextAllocation current, out ProcessContextAllocation downTiered);
}
