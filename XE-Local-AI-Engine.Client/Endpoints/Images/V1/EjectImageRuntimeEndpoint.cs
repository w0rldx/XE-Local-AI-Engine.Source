namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

public sealed class EjectImageRuntimeEndpoint(
    IImageServerSupervisor supervisor,
    IStableDiffusionInstalledRuntimeStore installedRuntimeStore) : Endpoint<ImageRuntimeActionRequest, ImageRuntimeStatusResponse>
{
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
        var result = await supervisor.EvictAllAsync(ct).ConfigureAwait(false);
        if (!result.Evicted)
        {
            await Send.ResultAsync(Results.Conflict(new ImageRuntimeBlockedResponse
            {
                Reason = "runtime-busy",
                Message = "Wait for active image jobs, image-runtime startup, or runtime mutation to finish before ejecting image processes.",
                Activity = result.Activity.ToResponse()
            })).ConfigureAwait(false);
            return;
        }

        var installed = await installedRuntimeStore.ReadAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ImageRuntimeStatusResponse
        {
            ManagedRuntime = installed?.ToResponse(),
            Activity = result.Activity.ToResponse()
        }, ct).ConfigureAwait(false);
    }
}
