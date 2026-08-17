namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Shared mapping of a <see cref="LlamaCppSourceBuildStartOutcome" /> to the stable machine reason code + user-safe
///     message the two start endpoints (generic source build and the CUDA-specific legacy route) return as a 409. Only
///     the build-kind noun differs between them, so both endpoints pass their own label and every message stays byte-for-byte
///     what it was before the two copies were folded together.
/// </summary>
internal static class LlamaCppSourceBuildStartEndpointSupport
{
    /// <summary>Build-kind label used by the CUDA-specific start endpoint ("A CUDA build...", "...the CUDA runtime").</summary>
    internal const string CudaBuildKind = "CUDA";

    /// <summary>Build-kind label used by the generic source-build start endpoint ("A source build...", "...the source runtime").</summary>
    internal const string SourceBuildKind = "source";

    /// <summary>
    ///     Returns the blocked reason code + message for a non-started outcome, or <see langword="null" /> when the build
    ///     actually started. <see cref="LlamaCppSourceBuildStartOutcome.ProcessesRunning" /> is the only outcome whose
    ///     response also carries the running-process count; the caller adds it.
    /// </summary>
    internal static BlockedBuild? MapBlocked(LlamaCppSourceBuildStartOutcome outcome, string buildKind)
    {
        return outcome switch
        {
            LlamaCppSourceBuildStartOutcome.AlreadyRunning => new BlockedBuild("already-building", $"A {buildKind} build is already in progress."),
            LlamaCppSourceBuildStartOutcome.InsufficientDisk => new BlockedBuild("disk", $"There is not enough free disk space to build the {buildKind} runtime."),
            LlamaCppSourceBuildStartOutcome.MissingPrerequisites => new BlockedBuild("prerequisites",
                "One or more build prerequisites are missing; resolve the checklist before building."),
            LlamaCppSourceBuildStartOutcome.ProcessesRunning => new BlockedBuild("processes-running",
                "Stop or eject all running llama.cpp models before building the runtime."),
            LlamaCppSourceBuildStartOutcome.RuntimeBusy => new BlockedBuild("runtime-busy",
                "Wait for the active llama.cpp source build or runtime change to finish before starting another build."),
            LlamaCppSourceBuildStartOutcome.Started => null,
            _ => throw new InvalidOperationException($"Unknown source-build start outcome: {outcome}.")
        };
    }

    /// <summary>The stable machine reason code and user-safe message for a start request that was refused.</summary>
    internal readonly record struct BlockedBuild(string Reason, string Message);
}
