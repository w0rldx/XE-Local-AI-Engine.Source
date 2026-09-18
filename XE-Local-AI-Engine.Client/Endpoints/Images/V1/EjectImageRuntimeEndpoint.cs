namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images;

public sealed class EjectImageRuntimeEndpoint : Endpoint<ImageRuntimeActionRequest, ImageRuntimeStatusResponse>
{
    private readonly ImageRuntimeOrchestrationService _imageRuntime;

    public EjectImageRuntimeEndpoint(ImageRuntimeOrchestrationService imageRuntime)
    {
        _imageRuntime = imageRuntime;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Images.RuntimeEject);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Accepts<ImageRuntimeActionRequest>("application/json")
                               .Produces<ImageRuntimeStatusResponse>(StatusCodes.Status200OK)
                               .Produces<ImageRuntimeBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(ImageRuntimeActionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await _imageRuntime.EvictAllAsync(ct);
        if (!result.Evicted)
        {
            await Send.ResultAsync(ImageRuntimeBlockedEndpointSupport.RuntimeBusy("Wait for active image jobs, image-runtime startup, or runtime mutation to finish before ejecting image processes.",
                          result.Activity));
            return;
        }

        var installed = await _imageRuntime.ReadInstalledRuntimeAsync(ct);
        await Send.OkAsync(new ImageRuntimeStatusResponse
        {
            ManagedRuntime = installed?.ToResponse(),
            Activity = result.Activity.ToResponse()
        }, ct);
    }
}
