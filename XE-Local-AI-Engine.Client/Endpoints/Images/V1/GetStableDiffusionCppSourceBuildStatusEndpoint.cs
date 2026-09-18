namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images;

public sealed class GetStableDiffusionCppSourceBuildStatusEndpoint : EndpointWithoutRequest<StableDiffusionCppSourceBuildStatusResponse>
{
    private readonly ImageRuntimeOrchestrationService _imageRuntime;

    public GetStableDiffusionCppSourceBuildStatusEndpoint(ImageRuntimeOrchestrationService imageRuntime)
    {
        _imageRuntime = imageRuntime;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Images.RuntimeSourceBuildStatus);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<StableDiffusionCppSourceBuildStatusResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        await Send.OkAsync(_imageRuntime.GetStatus().ToResponse(), ct);
    }
}
