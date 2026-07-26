namespace XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>Resolves and caches the single process-context allocation shared by footprint and launch.</summary>
public interface IProcessContextAllocationResolver
{
    Task<ProcessContextAllocation?> ResolveAsync(
        string modelName,
        ModelRole role,
        GpuVariant variant,
        ResolvedLaunchArguments resolved,
        CancellationToken ct);

    /// <summary>
    /// Returns the next smaller automatic hardware tier after a classified startup OOM. Frozen and deterministic
    /// override allocations are never changed. At most two lower tiers are issued for one cache identity.
    /// </summary>
    bool TryDownTierAfterOutOfMemory(ProcessContextAllocation current, out ProcessContextAllocation downTiered);
}
