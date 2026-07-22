namespace XE_Local_AI_Engine.Providers.LlamaServer.Implementation;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

internal sealed class LegacyCudaBuildServiceAdapter(ILlamaCppSourceBuildService sourceBuildService) : ICudaBuildService
{
    public async Task<CudaBuildStartOutcome> StartAsync(CancellationToken ct)
    {
        var outcome = await sourceBuildService.StartAsync(new LlamaCppSourceBuildRequest(
            LlamaCppSourceBackend.Cuda,
            LlamaCppSourceSelection.Official), ct).ConfigureAwait(false);
        return outcome == LlamaCppSourceBuildStartOutcome.Started ? CudaBuildStartOutcome.Started : CudaBuildStartOutcome.AlreadyRunning;
    }

    public CudaBuildStatus GetStatus()
    {
        var status = sourceBuildService.GetStatus();
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
        return sourceBuildService.GetStatus().CurrentBuild.IsLegacyPinnedCuda() && sourceBuildService.Cancel();
    }

    public void RecoverStaleWorkDirectory()
    {
        sourceBuildService.RecoverAsync(CancellationToken.None).GetAwaiter().GetResult();
    }
}
