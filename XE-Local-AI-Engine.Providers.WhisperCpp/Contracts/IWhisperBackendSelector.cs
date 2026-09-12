namespace XE_Local_AI_Engine.Providers.WhisperCpp.Contracts;

/// <summary>
///     Picks the whisper.cpp acceleration backend when no operator override and no validated managed source build has
///     already decided it. Does only as much hardware inspection as that decision needs, and does it by reusing the
///     shared provider-neutral hardware profiler rather than probing devices itself.
/// </summary>
/// <remarks>
///     The rule is short because upstream's asset matrix is short: NVIDIA on Windows selects
///     <see cref="WhisperBackend.Cuda" />, the one GPU prebuilt that exists; everything else selects
///     <see cref="WhisperBackend.Cpu" />, including NVIDIA on Linux, where the CUDA lane is the managed source build or
///     the bring-your-own override rather than a downloadable asset. An active override or managed-source signal
///     short-circuits the probe entirely.
/// </remarks>
public interface IWhisperBackendSelector
{
    /// <summary>Returns the active runtime backend, or probes the host to select an exact prebuilt backend.</summary>
    Task<WhisperBackend> SelectBackendAsync(CancellationToken ct);
}
