namespace XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>The warm Model Lab worker as the residents widget sees it: at most one row, and its eject.</summary>
/// <remarks>
///     <see cref="ModelLabExperimentService" /> is the node's source. The implementation raises the residency hub tick itself, through
///     <c>IRuntimeResidencyChangeNotifier</c>, on every change <see cref="GetResident" /> would show.
/// </remarks>
public interface IModelLabWarmResidentSource
{
    /// <summary>The worker held now, or <see langword="null" /> when none is. In-memory state only: no disk, no probe.</summary>
    ModelLabWarmResident? GetResident();

    /// <summary>
    ///     Evicts an Idle worker and reports whether it is gone, held by a job, or stopped without its exit being seen. A worker an
    ///     earlier eviction could not confirm stopped is reaped again and counts as not confirmed until that reap proves it gone.
    /// </summary>
    Task<ModelLabEjectOutcome> EjectAsync(CancellationToken cancellationToken);
}
