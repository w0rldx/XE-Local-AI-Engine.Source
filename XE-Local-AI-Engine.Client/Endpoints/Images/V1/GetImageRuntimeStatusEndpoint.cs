namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images;

public sealed class GetImageRuntimeStatusEndpoint : EndpointWithoutRequest<ImageRuntimeStatusResponse>
{
    private readonly ImageRuntimeOrchestrationService _imageRuntime;

    public GetImageRuntimeStatusEndpoint(ImageRuntimeOrchestrationService imageRuntime)
    {
        _imageRuntime = imageRuntime;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Images.Runtime);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<ImageRuntimeStatusResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var installed = await _imageRuntime.ReadInstalledRuntimeAsync(ct);
        await Send.OkAsync(new ImageRuntimeStatusResponse
        {
            ManagedRuntime = installed?.ToResponse(),
            Activity = _imageRuntime.GetActivitySnapshot().ToResponse()
        }, ct);
    }
}
