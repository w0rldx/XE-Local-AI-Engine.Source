namespace XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelFit.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelFit;

public sealed class GetLlamaCppSourceBuildStatusEndpoint(LlamaCppRuntimeOrchestrationService runtime)
    : EndpointWithoutRequest<LlamaCppSourceBuildStatusResponse>
{
    public override void Configure()
    {
        Get(LocalApiRoutes.ModelFit.SourceBuildStatus);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<LlamaCppSourceBuildStatusResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Send.OkAsync(runtime.GetSourceBuildStatus().ToResponse(), ct).ConfigureAwait(false);
    }
}
