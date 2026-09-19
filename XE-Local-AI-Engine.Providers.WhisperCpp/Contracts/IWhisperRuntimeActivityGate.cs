namespace XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>A process-wide snapshot of transcription-runtime activity that can conflict with runtime mutation.</summary>
public sealed class WhisperRuntimeActivitySnapshot
{
    public required int ActiveTranscriptionCount { get; init; }

    public required int SpawnReadinessCount { get; init; }

    public required int ResidentProcessCount { get; init; }

    public required bool MutationReserved { get; init; }

    public required bool EvictionReserved { get; init; }

    /// <summary>Whether anything at all is holding the runtime; the single flag the 409 envelope reports.</summary>
    public bool IsBusy =>
        MutationReserved
        || EvictionReserved
        || ActiveTranscriptionCount > 0
        || SpawnReadinessCount > 0
        || ResidentProcessCount > 0;
}

/// <summary>An identity-scoped activity lease. Disposal releases exactly the lease that was granted, once.</summary>
public interface IWhisperRuntimeActivityLease : IAsyncDisposable, IDisposable;

/// <summary>
///     Atomically coordinates in-flight transcriptions, spawn/readiness windows, resident processes, and exclusive
///     runtime mutation. A mutation reservation is granted only when every activity count is zero; while one is held,
///     new transcription and spawn leases are refused.
/// </summary>
/// <remarks>
///     This is what answers <c>409 runtime-busy</c>: an eject, a managed source build or a source-build remove issued
///     while a transcription is in flight is refused here rather than racing the daemon that is serving it.
/// </remarks>
public interface IWhisperRuntimeActivityGate
{
    WhisperRuntimeActivitySnapshot GetSnapshot();

    /// <summary>Held for the duration of one in-flight transcription.</summary>
    IWhisperRuntimeActivityLease? TryAcquireTranscriptionLease();

    /// <summary>Held across a spawn and its readiness wait.</summary>
    IWhisperRuntimeActivityLease? TryAcquireSpawnReadinessLease();

    /// <summary>Held for as long as a daemon process is resident; released only once the child is actually down.</summary>
    IWhisperRuntimeActivityLease? TryAcquireResidentProcessLease();

    /// <summary>Exclusive against transcriptions and spawns. Taken by an eject.</summary>
    IWhisperRuntimeActivityLease? TryAcquireEvictionReservation();

    /// <summary>Exclusive against everything, resident processes included. Taken by a managed-runtime mutation.</summary>
    IWhisperRuntimeActivityLease? TryAcquireMutationReservation();
}
