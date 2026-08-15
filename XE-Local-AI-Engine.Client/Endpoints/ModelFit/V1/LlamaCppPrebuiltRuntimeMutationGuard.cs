namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

internal static class LlamaCppPrebuiltRuntimeMutationGuard
{
    internal const string KeepModelWarmBlockedMessage =
        "Disable Keep Model Warm before changing the llama.cpp runtime, then eject any running models and retry.";

    internal static bool IsSourceBuildActive(ILlamaCppSourceBuildActivity sourceBuildActivity)
    {
        return sourceBuildActivity.ActiveBuildId is not null;
    }

    internal static Task<bool> IsKeepModelWarmEnabledAsync(INodeRuntimeSettings nodeRuntimeSettings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(nodeRuntimeSettings);
        return nodeRuntimeSettings.GetKeepModelWarmEnabledAsync(ct);
    }

    /// <summary>
    ///     Shared remove gate for the managed source-build runtimes (generic + CUDA). Refuses while a source build is
    ///     active — re-checked AFTER the mutation lease is taken so a build that starts during acquisition still blocks —
    ///     refuses when the lease cannot be taken or any llama-server process is still running (eject-first), and only
    ///     then runs <paramref name="removeAsync" /> while holding the lease. The lease is disposed on every path.
    /// </summary>
    internal static async Task<(bool Removed, int RunningProcessCount, bool BuildActive)> TryRemoveAsync(ILlamaServerProcessSupervisor processSupervisor,
        ILlamaCppSourceBuildActivity sourceBuildActivity,
        Func<CancellationToken, Task> removeAsync,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(processSupervisor);
        ArgumentNullException.ThrowIfNull(removeAsync);

        if (IsSourceBuildActive(sourceBuildActivity))
        {
            return (false, 0, true);
        }

        await using var mutationLease = await processSupervisor.TryAcquireRuntimeMutationLeaseAsync(ct).ConfigureAwait(false);
        if (IsSourceBuildActive(sourceBuildActivity))
        {
            return (false, 0, true);
        }

        var runningProcessCount = processSupervisor.CountRunningProcesses();
        if (mutationLease is null || runningProcessCount > 0)
        {
            return (false, runningProcessCount, false);
        }

        await removeAsync(ct).ConfigureAwait(false);
        return (true, runningProcessCount, false);
    }

    internal static async Task<(ILlamaServerRuntimeMutationLease? Lease, int RunningProcessCount, string? BlockedMessage)> TryAcquireAsync(IInstalledRuntimeStore installedRuntimeStore,
        ILlamaCppSourceBuildActivity sourceBuildActivity,
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

        if (IsSourceBuildActive(sourceBuildActivity))
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
