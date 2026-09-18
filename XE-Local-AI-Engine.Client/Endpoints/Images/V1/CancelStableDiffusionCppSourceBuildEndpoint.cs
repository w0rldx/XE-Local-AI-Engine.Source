namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images;

public sealed class CancelStableDiffusionCppSourceBuildEndpoint : Endpoint<ImageRuntimeActionRequest, StableDiffusionCppSourceBuildStatusResponse>
{
    private readonly ImageRuntimeOrchestrationService _imageRuntime;

    public CancelStableDiffusionCppSourceBuildEndpoint(ImageRuntimeOrchestrationService imageRuntime)
    {
        _imageRuntime = imageRuntime;
    }

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
        _ = _imageRuntime.Cancel();
        await Send.OkAsync(_imageRuntime.GetStatus().ToResponse(), ct);
    }
}
