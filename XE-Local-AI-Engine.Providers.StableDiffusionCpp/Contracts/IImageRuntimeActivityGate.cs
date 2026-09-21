namespace XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

/// <summary>A process-wide snapshot of image-runtime activity that can conflict with runtime mutation.</summary>
public sealed class ImageRuntimeActivitySnapshot
{
    public required int ActiveJobCount { get; init; }

    public required int SpawnReadinessCount { get; init; }

    public required int ResidentProcessCount { get; init; }

    public required bool MutationReserved { get; init; }

    public required bool EvictionReserved { get; init; }

    public bool IsBusy => MutationReserved || EvictionReserved || ActiveJobCount > 0 || SpawnReadinessCount > 0 || ResidentProcessCount > 0;
}

/// <summary>An identity-scoped activity lease. Disposal releases exactly the lease that was granted.</summary>
public interface IImageRuntimeActivityLease : IAsyncDisposable, IDisposable;

/// <summary>
///     Atomically coordinates long-lived image jobs, spawn/readiness windows, resident processes, and exclusive runtime
///     mutation.
/// </summary>
/// <remarks>
///     A mutation reservation is granted only when all activity counts are zero; while reserved, new job and spawn
///     leases are refused.
/// </remarks>
public interface IImageRuntimeActivityGate
{
    ImageRuntimeActivitySnapshot GetSnapshot();
    IImageRuntimeActivityLease? TryAcquireJobLease();
    IImageRuntimeActivityLease? TryAcquireSpawnReadinessLease();
    IImageRuntimeActivityLease? TryAcquireResidentProcessLease();
    IImageRuntimeActivityLease? TryAcquireEvictionReservation();
    IImageRuntimeActivityLease? TryAcquireMutationReservation();
}
