namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images;

public sealed class GetStableDiffusionCppSourceBuildPrerequisitesEndpoint(ImageRuntimeOrchestrationService imageRuntime)
    : Endpoint<GetStableDiffusionCppSourceBuildPrerequisitesRequest, StableDiffusionCppSourceBuildPrerequisitesResponse>
{
    public override void Configure()
    {
        Get(LocalApiRoutes.Images.RuntimeSourceBuildPrerequisites);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<StableDiffusionCppSourceBuildPrerequisitesResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(GetStableDiffusionCppSourceBuildPrerequisitesRequest request, CancellationToken ct)
    {
        var backend = request.Backend.ToContract();
        var report = await imageRuntime.ProbeAsync(backend, ct);
        await Send.OkAsync(report.ToResponse(backend), ct);
    }
}
