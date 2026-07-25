namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

public sealed class CancelStableDiffusionCppSourceBuildEndpoint(IStableDiffusionCppSourceBuildService buildService)
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
        _ = buildService.Cancel();
        await Send.OkAsync(buildService.GetStatus().ToResponse(), ct).ConfigureAwait(false);
    }
}
