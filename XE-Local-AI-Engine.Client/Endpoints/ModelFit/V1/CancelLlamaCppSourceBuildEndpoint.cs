namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.LlamaServer.Contracts;

public sealed class CancelLlamaCppSourceBuildEndpoint(ILlamaCppSourceBuildService buildService)
    : EndpointWithoutRequest<LlamaCppSourceBuildStatusResponse>
{
    public override void Configure()
    {
        Post(LocalApiRoutes.ModelFit.SourceBuildCancel);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        buildService.Cancel();
        await Send.OkAsync(buildService.GetStatus().ToResponse(), ct).ConfigureAwait(false);
    }
}
