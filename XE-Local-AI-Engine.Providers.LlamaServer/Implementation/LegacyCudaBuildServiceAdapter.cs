namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

internal sealed class LegacyCudaBuildServiceAdapter : ICudaBuildService
{
    private readonly ILlamaCppSourceBuildService _sourceBuildService;

    public LegacyCudaBuildServiceAdapter(ILlamaCppSourceBuildService sourceBuildService)
    {
        _sourceBuildService = sourceBuildService;
    }

    public async Task<CudaBuildStartOutcome> StartAsync(CancellationToken ct)
    {
        var result = await _sourceBuildService.StartAsync(new LlamaCppSourceBuildRequest(LlamaCppSourceBackend.Cuda,
            LlamaCppSourceSelection.Official), ct).ConfigureAwait(false);
        return result.Outcome switch
        {
            LlamaCppSourceBuildStartOutcome.Started => CudaBuildStartOutcome.Started,
            LlamaCppSourceBuildStartOutcome.AlreadyRunning => CudaBuildStartOutcome.AlreadyRunning,
            LlamaCppSourceBuildStartOutcome.InsufficientDisk => throw new LlamaRuntimeException("There is not enough free disk space to build the CUDA runtime."),
            LlamaCppSourceBuildStartOutcome.MissingPrerequisites => throw new LlamaRuntimeException("One or more build prerequisites are missing; resolve the checklist before building."),
            LlamaCppSourceBuildStartOutcome.ProcessesRunning => throw new LlamaRuntimeException("Stop or eject all running llama.cpp models before building the runtime."),
            LlamaCppSourceBuildStartOutcome.RuntimeBusy => throw new LlamaRuntimeException("Wait for the active llama.cpp source build or runtime change to finish before starting another build."),
            _ => throw new InvalidOperationException($"Unknown source-build start outcome: {result.Outcome}.")
        };
    }

    public CudaBuildStatus GetStatus()
    {
        var status = _sourceBuildService.GetStatus();
        if (!status.CurrentBuild.IsLegacyPinnedCuda())
        {
            return new CudaBuildStatus(CudaBuildPhase.Idle, false, false, [], null, null, null, null);
        }

        return new CudaBuildStatus((CudaBuildPhase)(int)status.Phase,
            status.IsRunning,
            status.Terminal,
            status.LogLines,
            status.SanitizedError,
            LlamaCppReleasePins.PinnedTag,
            status.StartedAtUtc,
            status.CompletedAtUtc);
    }

    public bool Cancel()
    {
        return _sourceBuildService.CancelLegacyPinnedCuda();
    }

    public async Task RecoverStaleWorkDirectoryAsync(CancellationToken cancellationToken)
    {
        await _sourceBuildService.RecoverAsync(cancellationToken).ConfigureAwait(false);
    }
}
