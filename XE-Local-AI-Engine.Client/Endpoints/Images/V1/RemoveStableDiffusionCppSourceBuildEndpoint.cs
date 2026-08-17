namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

public sealed class RemoveStableDiffusionCppSourceBuildEndpoint(
    IStableDiffusionCppSourceBuildService buildService,
    IStableDiffusionInstalledRuntimeStore installedRuntimeStore,
    IImageRuntimeActivityGate activityGate) : Endpoint<ImageRuntimeActionRequest, ImageRuntimeStatusResponse>
{
    public override void Configure()
    {
        Post(LocalApiRoutes.Images.RuntimeSourceBuildRemove);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Accepts<ImageRuntimeActionRequest>("application/json")
                               .Produces<ImageRuntimeStatusResponse>(StatusCodes.Status200OK)
                               .Produces<ImageRuntimeBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(ImageRuntimeActionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await buildService.RemoveAsync(ct).ConfigureAwait(false);
        if (result.Outcome == StableDiffusionCppSourceBuildRemoveOutcome.RuntimeBusy)
        {
            await Send.ResultAsync(ImageRuntimeBlockedEndpointSupport.RuntimeBusy(
                          "Wait for active image jobs and image-runtime processes to finish before removing the managed runtime.",
                          result.Activity ?? activityGate.GetSnapshot()))
                      .ConfigureAwait(false);
            return;
        }

        if (result.Outcome is not (StableDiffusionCppSourceBuildRemoveOutcome.Removed
            or StableDiffusionCppSourceBuildRemoveOutcome.NotInstalled))
        {
            throw new InvalidOperationException($"Unknown stable-diffusion.cpp source-build remove outcome: {result.Outcome}.");
        }

        var installed = await installedRuntimeStore.ReadAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ImageRuntimeStatusResponse
        {
            ManagedRuntime = installed?.ToResponse(),
            Activity = activityGate.GetSnapshot().ToResponse()
        }, ct).ConfigureAwait(false);
    }
}
