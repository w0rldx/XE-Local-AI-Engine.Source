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

    /// <summary>
    ///     The result of the shared remove gate: whether the removal ran, how many llama-server processes were still
    ///     running when the gate was evaluated, and whether a source build blocked it.
    /// </summary>
    internal sealed record RemovalOutcome(bool Removed, int RunningProcessCount, bool BuildActive);
}
