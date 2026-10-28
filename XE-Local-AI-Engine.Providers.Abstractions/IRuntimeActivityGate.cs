namespace XE_Local_AI_Engine.Providers.Abstractions;

/// <summary>
///     Atomically coordinates in-flight jobs (a transcription, an image generation), spawn/readiness windows, resident
///     processes, and exclusive runtime mutation.
/// </summary>
/// <remarks>
///     A mutation reservation is granted only when every activity count is zero; while one is held, new job and
///     spawn leases are refused.
///     This is what answers <c>409 runtime-busy</c>: an eject, a managed source build or a source-build remove issued
///     while a job is in flight is refused here rather than racing the daemon that is serving it.
/// </remarks>
public interface IRuntimeActivityGate
{
    RuntimeActivitySnapshot GetSnapshot();

    /// <summary>Held for the duration of one in-flight job.</summary>
    IRuntimeActivityLease? TryAcquireJobLease();

    /// <summary>Held across a spawn and its readiness wait.</summary>
    IRuntimeActivityLease? TryAcquireSpawnReadinessLease();

    /// <summary>Held for as long as a daemon process is resident; released only once the child is actually down.</summary>
    IRuntimeActivityLease? TryAcquireResidentProcessLease();

    /// <summary>Exclusive against jobs and spawns. Taken by an eject.</summary>
    IRuntimeActivityLease? TryAcquireEvictionReservation();

    /// <summary>Exclusive against everything, resident processes included. Taken by a managed-runtime mutation.</summary>
    IRuntimeActivityLease? TryAcquireMutationReservation();
}
