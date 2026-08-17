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
    internal static async Task<RemovalOutcome> TryRemoveAsync(ILlamaServerProcessSupervisor processSupervisor,
        ILlamaCppSourceBuildActivity sourceBuildActivity,
        Func<CancellationToken, Task> removeAsync,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(processSupervisor);
        ArgumentNullException.ThrowIfNull(removeAsync);

        if (IsSourceBuildActive(sourceBuildActivity))
        {
            return new RemovalOutcome(Removed: false, RunningProcessCount: 0, BuildActive: true);
        }

        await using var mutationLease = await processSupervisor.TryAcquireRuntimeMutationLeaseAsync(ct).ConfigureAwait(false);
        if (IsSourceBuildActive(sourceBuildActivity))
        {
            return new RemovalOutcome(Removed: false, RunningProcessCount: 0, BuildActive: true);
        }

        var runningProcessCount = processSupervisor.CountRunningProcesses();
        if (mutationLease is null || runningProcessCount > 0)
        {
            return new RemovalOutcome(Removed: false, runningProcessCount, BuildActive: false);
        }

        await removeAsync(ct).ConfigureAwait(false);
        return new RemovalOutcome(Removed: true, runningProcessCount, BuildActive: false);
    }

    internal static async Task<LeaseAcquisition> TryAcquireAsync(IInstalledRuntimeStore installedRuntimeStore,
        ILlamaCppSourceBuildActivity sourceBuildActivity,
        ILlamaServerProcessSupervisor processSupervisor,
        CancellationToken ct)
    {
        var lease = await processSupervisor.TryAcquireRuntimeMutationLeaseAsync(ct).ConfigureAwait(false);
        if (lease is null)
        {
            return new LeaseAcquisition(Lease: null, processSupervisor.CountRunningProcesses(),
                "The llama.cpp runtime is busy with another build or runtime change. Try again after it completes.");
        }

        var installed = await installedRuntimeStore.ReadAsync(ct).ConfigureAwait(false);
        if (installed?.SourceBuildPath is { Length: > 0 })
        {
            return new LeaseAcquisition(lease, processSupervisor.CountRunningProcesses(),
                "Remove the installed source-built llama.cpp runtime before installing a prebuilt runtime.");
        }

        if (IsSourceBuildActive(sourceBuildActivity))
        {
            return new LeaseAcquisition(lease, processSupervisor.CountRunningProcesses(),
                "Wait for the active llama.cpp source build to finish or cancel it before installing a prebuilt runtime.");
        }

        var runningProcessCount = processSupervisor.CountRunningProcesses();
        return runningProcessCount > 0
            ? new LeaseAcquisition(lease, runningProcessCount, "Stop or eject all running llama.cpp models before updating the runtime.")
            : new LeaseAcquisition(lease, runningProcessCount, BlockedMessage: null);
    }

    /// <summary>
    ///     The result of the shared remove gate: whether the removal ran, how many llama-server processes were still
    ///     running when the gate was evaluated, and whether a source build blocked it.
    /// </summary>
    internal sealed record RemovalOutcome(bool Removed, int RunningProcessCount, bool BuildActive);

    /// <summary>
    ///     The result of the prebuilt-install gate: the mutation lease (already taken when non-null, so the caller owns
    ///     disposing it even on a blocked result), the running-process count, and the refusal message when blocked.
    /// </summary>
    internal sealed record LeaseAcquisition(ILlamaServerRuntimeMutationLease? Lease, int RunningProcessCount, string? BlockedMessage);
}
