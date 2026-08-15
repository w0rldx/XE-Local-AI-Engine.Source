namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

/// <summary>
///     Removes the adopted managed CUDA source build (POST model-fit/llamacpp/cuda-build/remove). Eject-first gated: the
///     build cannot be removed while a llama-server process holds a binary (409). On removal the on-disk build tree is
///     deleted (path-guarded to <c>{cacheRoot}/llama.cpp/source-cuda/</c>), the installed-runtime record + the managed
///     signal are cleared, the local chat-client cache is invalidated, and the refreshed runtime status is returned.
/// </summary>
public sealed class RemoveCudaBuildEndpoint(
    ILlamaCppBinaryManager binaryManager,
    ILlamaServerProcessSupervisor processSupervisor,
    ILlamaCppSourceBuildActivity sourceBuildActivity,
    ILlamaCppUpdateState updateState,
    IInstalledRuntimeStore installedRuntimeStore,
    INodeRuntimeSettings nodeRuntimeSettings,
    ILocalChatClientCacheInvalidator localChatClientCacheInvalidator) : EndpointWithoutRequest<LlamaCppRuntimeStatusResponse>
{
    private readonly ILlamaCppBinaryManager _binaryManager = binaryManager ?? throw new ArgumentNullException(nameof(binaryManager));
    private readonly IInstalledRuntimeStore _installedRuntimeStore = installedRuntimeStore ?? throw new ArgumentNullException(nameof(installedRuntimeStore));
    private readonly ILocalChatClientCacheInvalidator _localChatClientCacheInvalidator = localChatClientCacheInvalidator ?? throw new ArgumentNullException(nameof(localChatClientCacheInvalidator));
    private readonly INodeRuntimeSettings _nodeRuntimeSettings = nodeRuntimeSettings ?? throw new ArgumentNullException(nameof(nodeRuntimeSettings));
    private readonly ILlamaServerProcessSupervisor _processSupervisor = processSupervisor ?? throw new ArgumentNullException(nameof(processSupervisor));
    private readonly ILlamaCppUpdateState _updateState = updateState ?? throw new ArgumentNullException(nameof(updateState));

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.CudaBuildRemove);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<LlamaCppRuntimeStatusResponse>(StatusCodes.Status200OK)
                               .Produces<CudaBuildBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (await LlamaCppPrebuiltRuntimeMutationGuard
                  .IsKeepModelWarmEnabledAsync(_nodeRuntimeSettings, ct)
                  .ConfigureAwait(false))
        {
            await Send.ResultAsync(Results.Conflict(new CudaBuildBlockedResponse
            {
                Reason = "keep-model-warm-enabled",
                Message = LlamaCppPrebuiltRuntimeMutationGuard.KeepModelWarmBlockedMessage,
                RunningProcessCount = _processSupervisor.CountRunningProcesses()
            })).ConfigureAwait(false);
            return;
        }

        var (removed, runningProcessCount, buildActive) = await LlamaCppPrebuiltRuntimeMutationGuard
                                                                 .TryRemoveAsync(_processSupervisor, sourceBuildActivity, _binaryManager.RemoveCudaSourceBuildAsync, ct)
                                                                 .ConfigureAwait(false);
        if (!removed)
        {
            await Send.ResultAsync(Results.Conflict(new CudaBuildBlockedResponse
            {
                Reason = buildActive ? "already-building" : "processes-running",
                Message = buildActive
                    ? "Wait for the active llama.cpp source build to finish or cancel it before removing the runtime."
                    : "Stop or eject all running llama.cpp models before removing the runtime.",
                RunningProcessCount = runningProcessCount
            })).ConfigureAwait(false);
            return;
        }

        // The runtime record is gone; a cached deferred chat client may still point at the removed binary's endpoint.
        _localChatClientCacheInvalidator.ClearClientCache();

        var recommendedTag = await _nodeRuntimeSettings.GetRecommendedLlamaCppTagAsync(ct).ConfigureAwait(false);
        var installed = await _installedRuntimeStore.ReadAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(_updateState.Current.ToRuntimeStatusResponse(installed, recommendedTag, runningProcessCount), ct).ConfigureAwait(false);
    }
}
