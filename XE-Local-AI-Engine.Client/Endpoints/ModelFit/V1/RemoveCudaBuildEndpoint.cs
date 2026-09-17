namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

/// <summary>
///     Removes the adopted managed CUDA source build (POST model-fit/llamacpp/cuda-build/remove). Eject-first gated: the
///     build cannot be removed while a llama-server process holds a binary (409). On removal the on-disk build tree is
///     deleted (path-guarded to <c>{cacheRoot}/llama.cpp/source-cuda/</c>), the installed-runtime record + the managed
///     signal are cleared, the local chat-client cache is invalidated, and the refreshed runtime status is returned.
/// </summary>
public sealed class RemoveCudaBuildEndpoint(
    LlamaCppRuntimeOrchestrationService runtime,
    INodeRuntimeSettings nodeRuntimeSettings,
    ILocalChatClientCacheInvalidator localChatClientCacheInvalidator) : EndpointWithoutRequest<LlamaCppRuntimeStatusResponse>
{
    private readonly ILocalChatClientCacheInvalidator _localChatClientCacheInvalidator = localChatClientCacheInvalidator ?? throw new ArgumentNullException(nameof(localChatClientCacheInvalidator));
    private readonly INodeRuntimeSettings _nodeRuntimeSettings = nodeRuntimeSettings ?? throw new ArgumentNullException(nameof(nodeRuntimeSettings));
    private readonly LlamaCppRuntimeOrchestrationService _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

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
                RunningProcessCount = _runtime.CountRunningProcesses()
            })).ConfigureAwait(false);
            return;
        }

        var (removed, runningProcessCount, buildActive) = await _runtime.TryRemoveCudaBuildAsync(ct).ConfigureAwait(false);
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
        var installed = await _runtime.ReadInstalledRuntimeAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(_runtime.CurrentUpdateSnapshot.ToRuntimeStatusResponse(installed, recommendedTag, runningProcessCount), ct).ConfigureAwait(false);
    }
}
