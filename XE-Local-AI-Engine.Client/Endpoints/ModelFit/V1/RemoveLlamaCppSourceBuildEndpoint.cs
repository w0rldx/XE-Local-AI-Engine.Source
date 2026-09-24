namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

public sealed class RemoveLlamaCppSourceBuildEndpoint : EndpointWithoutRequest<LlamaCppRuntimeStatusResponse>
{
    private readonly LlamaCppRuntimeOrchestrationService _runtime;
    private readonly INodeRuntimeSettings _nodeRuntimeSettings;
    private readonly ILocalChatClientCacheInvalidator _localChatClientCacheInvalidator;

    public RemoveLlamaCppSourceBuildEndpoint(LlamaCppRuntimeOrchestrationService runtime,
        INodeRuntimeSettings nodeRuntimeSettings,
        ILocalChatClientCacheInvalidator localChatClientCacheInvalidator)
    {
        _runtime = runtime;
        _nodeRuntimeSettings = nodeRuntimeSettings;
        _localChatClientCacheInvalidator = localChatClientCacheInvalidator;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.SourceBuildRemove);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<LlamaCppRuntimeStatusResponse>(StatusCodes.Status200OK)
                               .Produces<LlamaCppSourceBuildBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (await LlamaCppPrebuiltRuntimeMutationGuard
                .IsKeepModelWarmEnabledAsync(_nodeRuntimeSettings, ct))
        {
            await Send.ResultAsync(Results.Conflict(new LlamaCppSourceBuildBlockedResponse
            {
                Reason = "keep-model-warm-enabled",
                Message = LlamaCppPrebuiltRuntimeMutationGuard.KeepModelWarmBlockedMessage,
                RunningProcessCount = _runtime.CountRunningProcesses()
            }));
            return;
        }

        var (removed, runningProcessCount, buildActive) = await _runtime.TryRemoveSourceBuildAsync(ct);
        if (!removed)
        {
            await Send.ResultAsync(Results.Conflict(new LlamaCppSourceBuildBlockedResponse
            {
                Reason = buildActive ? "already-building" : "processes-running",
                Message = buildActive
                    ? "Wait for the active llama.cpp source build to finish or cancel it before removing the runtime."
                    : "Stop or eject all running llama.cpp models before removing the runtime.",
                RunningProcessCount = runningProcessCount
            }));
            return;
        }

        _localChatClientCacheInvalidator.ClearClientCache();
        var recommendedTag = await _nodeRuntimeSettings.GetRecommendedLlamaCppTagAsync(ct);
        var installed = await _runtime.ReadInstalledRuntimeAsync(ct);
        await Send.OkAsync(_runtime.CurrentUpdateSnapshot.ToRuntimeStatusResponse(installed, recommendedTag, runningProcessCount), ct);
    }
}
