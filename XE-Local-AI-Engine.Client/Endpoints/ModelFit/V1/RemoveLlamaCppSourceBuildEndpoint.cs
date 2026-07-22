namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.CloudProviders;
using XE_Local_AI_Engine.Client.Services.NodeSettings;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

public sealed class RemoveLlamaCppSourceBuildEndpoint(
    ILlamaCppBinaryManager binaryManager,
    ILlamaServerProcessSupervisor processSupervisor,
    ILlamaCppUpdateState updateState,
    IInstalledRuntimeStore installedRuntimeStore,
    INodeRuntimeSettings nodeRuntimeSettings,
    ILocalChatClientCacheInvalidator localChatClientCacheInvalidator) : EndpointWithoutRequest<LlamaCppRuntimeStatusResponse>
{
    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.SourceBuildRemove);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var runningProcessCount = processSupervisor.CountRunningProcesses();
        if (runningProcessCount > 0)
        {
            await Send.ResultAsync(Results.Conflict(new LlamaCppSourceBuildBlockedResponse
            {
                Reason = "processes-running",
                Message = "Stop or eject all running llama.cpp models before removing the runtime.",
                RunningProcessCount = runningProcessCount
            })).ConfigureAwait(false);
            return;
        }

        await binaryManager.RemoveSourceBuildAsync(ct).ConfigureAwait(false);
        localChatClientCacheInvalidator.ClearClientCache();
        var recommendedTag = await nodeRuntimeSettings.GetRecommendedLlamaCppTagAsync(ct).ConfigureAwait(false);
        var installed = await installedRuntimeStore.ReadAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(updateState.Current.ToRuntimeStatusResponse(installed, recommendedTag, runningProcessCount), ct).ConfigureAwait(false);
    }
}
