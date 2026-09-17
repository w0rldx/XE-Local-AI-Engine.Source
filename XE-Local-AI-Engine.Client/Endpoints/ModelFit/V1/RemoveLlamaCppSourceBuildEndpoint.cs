namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.ModelFit;
using XE_Local_AI_Engine.Client.Services.NodeSettings;

public sealed class RemoveLlamaCppSourceBuildEndpoint(
    LlamaCppRuntimeOrchestrationService runtime,
    INodeRuntimeSettings nodeRuntimeSettings,
    ILocalChatClientCacheInvalidator localChatClientCacheInvalidator) : EndpointWithoutRequest<LlamaCppRuntimeStatusResponse>
{
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
                  .IsKeepModelWarmEnabledAsync(nodeRuntimeSettings, ct))
        {
            await Send.ResultAsync(Results.Conflict(new LlamaCppSourceBuildBlockedResponse
            {
                Reason = "keep-model-warm-enabled",
                Message = LlamaCppPrebuiltRuntimeMutationGuard.KeepModelWarmBlockedMessage,
                RunningProcessCount = runtime.CountRunningProcesses()
            }));
            return;
        }

        var (removed, runningProcessCount, buildActive) = await runtime.TryRemoveSourceBuildAsync(ct);
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

        localChatClientCacheInvalidator.ClearClientCache();
        var recommendedTag = await nodeRuntimeSettings.GetRecommendedLlamaCppTagAsync(ct);
        var installed = await runtime.ReadInstalledRuntimeAsync(ct);
        await Send.OkAsync(runtime.CurrentUpdateSnapshot.ToRuntimeStatusResponse(installed, recommendedTag, runningProcessCount), ct);
    }
}
