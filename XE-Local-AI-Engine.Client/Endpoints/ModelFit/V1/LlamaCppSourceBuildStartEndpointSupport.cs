namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Shared mapping of a <see cref="LlamaCppSourceBuildStartOutcome" /> to the stable machine reason code and
///     user-safe message the source-build start endpoint returns as a 409.
/// </summary>
/// <remarks>
///     The build-kind noun is a parameter, so each surface keeps its exact wording and a second start surface can reuse
///     the mapping without duplicating the outcome switch.
/// </remarks>
internal static class LlamaCppSourceBuildStartEndpointSupport
{
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
