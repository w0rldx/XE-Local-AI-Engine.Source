namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images;

public sealed class GetImageRuntimeStatusEndpoint(ImageRuntimeOrchestrationService imageRuntime)
    : EndpointWithoutRequest<ImageRuntimeStatusResponse>
{
    public override void Configure()
    {
        Get(LocalApiRoutes.Images.Runtime);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<ImageRuntimeStatusResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var installed = await imageRuntime.ReadInstalledRuntimeAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ImageRuntimeStatusResponse
        {
            ManagedRuntime = installed?.ToResponse(),
            Activity = imageRuntime.GetActivitySnapshot().ToResponse()
        }, ct).ConfigureAwait(false);
    }
}
