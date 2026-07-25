namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

public sealed class GetStableDiffusionCppSourceBuildStatusEndpoint(IStableDiffusionCppSourceBuildService buildService)
    : EndpointWithoutRequest<StableDiffusionCppSourceBuildStatusResponse>
{
    public override void Configure()
    {
        Get(LocalApiRoutes.Images.RuntimeSourceBuildStatus);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<StableDiffusionCppSourceBuildStatusResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Send.OkAsync(buildService.GetStatus().ToResponse(), ct).ConfigureAwait(false);
    }
}
