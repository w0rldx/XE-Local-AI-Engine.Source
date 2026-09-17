namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images;

public sealed class CancelStableDiffusionCppSourceBuildEndpoint(ImageRuntimeOrchestrationService imageRuntime)
    : Endpoint<ImageRuntimeActionRequest, StableDiffusionCppSourceBuildStatusResponse>
{
    public override void Configure()
    {
        Post(LocalApiRoutes.Images.RuntimeSourceBuildCancel);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Accepts<ImageRuntimeActionRequest>("application/json")
                               .Produces<StableDiffusionCppSourceBuildStatusResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(ImageRuntimeActionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        _ = imageRuntime.Cancel();
        await Send.OkAsync(imageRuntime.GetStatus().ToResponse(), ct);
    }
}
