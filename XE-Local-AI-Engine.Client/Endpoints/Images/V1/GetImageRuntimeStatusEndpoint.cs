namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

public sealed class GetImageRuntimeStatusEndpoint(
    IStableDiffusionInstalledRuntimeStore installedRuntimeStore,
    IImageRuntimeActivityGate activityGate) : EndpointWithoutRequest<ImageRuntimeStatusResponse>
{
    public override void Configure()
    {
        Get(LocalApiRoutes.Images.Runtime);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<ImageRuntimeStatusResponse>(StatusCodes.Status200OK));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var installed = await installedRuntimeStore.ReadAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ImageRuntimeStatusResponse
        {
            ManagedRuntime = installed?.ToResponse(),
            Activity = activityGate.GetSnapshot().ToResponse()
        }, ct).ConfigureAwait(false);
    }
}
