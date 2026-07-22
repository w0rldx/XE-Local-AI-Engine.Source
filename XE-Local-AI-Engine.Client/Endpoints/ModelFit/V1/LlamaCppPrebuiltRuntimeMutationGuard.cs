namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

internal static class LlamaCppPrebuiltRuntimeMutationGuard
{
    internal static async Task<(ILlamaServerRuntimeMutationLease? Lease, int RunningProcessCount, string? BlockedMessage)> TryAcquireAsync(
        IInstalledRuntimeStore installedRuntimeStore,
        ILlamaCppSourceBuildService sourceBuildService,
        ILlamaServerProcessSupervisor processSupervisor,
        CancellationToken ct)
    {
        var lease = await processSupervisor.TryAcquireRuntimeMutationLeaseAsync(ct).ConfigureAwait(false);
        if (lease is null)
        {
            return (null, processSupervisor.CountRunningProcesses(),
                "The llama.cpp runtime is busy with another build or runtime change. Try again after it completes.");
        }

        var installed = await installedRuntimeStore.ReadAsync(ct).ConfigureAwait(false);
        if (installed?.SourceBuildPath is { Length: > 0 })
        {
            return (lease, processSupervisor.CountRunningProcesses(),
                "Remove the installed source-built llama.cpp runtime before installing a prebuilt runtime.");
        }

        if (sourceBuildService.GetStatus().IsRunning)
        {
            return (lease, processSupervisor.CountRunningProcesses(),
                "Wait for the active llama.cpp source build to finish or cancel it before installing a prebuilt runtime.");
        }

        var runningProcessCount = processSupervisor.CountRunningProcesses();
        return runningProcessCount > 0
            ? (lease, runningProcessCount, "Stop or eject all running llama.cpp models before updating the runtime.")
            : (lease, runningProcessCount, null);
    }
}
