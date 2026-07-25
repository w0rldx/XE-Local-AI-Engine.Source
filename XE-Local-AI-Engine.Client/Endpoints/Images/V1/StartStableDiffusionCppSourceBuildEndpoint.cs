namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

public sealed class StartStableDiffusionCppSourceBuildEndpoint(
    IStableDiffusionCppSourceBuildService buildService,
    IImageRuntimeActivityGate activityGate)
    : Endpoint<StartStableDiffusionCppSourceBuildRequest, StartStableDiffusionCppSourceBuildResponse>
{
    public override void Configure()
    {
        Post(LocalApiRoutes.Images.RuntimeSourceBuild);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<StartStableDiffusionCppSourceBuildResponse>(StatusCodes.Status200OK)
                               .ProducesProblemFE(StatusCodes.Status400BadRequest)
                               .Produces<ImageRuntimeBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(StartStableDiffusionCppSourceBuildRequest request, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
        {
            await BlockAsync("not-linux", "In-app source builds are available on Linux only.", activityGate.GetSnapshot()).ConfigureAwait(false);
            return;
        }

        try
        {
            var result = await buildService.StartAsync(request.ToContract(), ct).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case StableDiffusionCppSourceBuildStartOutcome.AlreadyRunning:
                    await BlockAsync(
                            "already-building",
                            "A stable-diffusion.cpp source build is already in progress.",
                            result.Activity ?? activityGate.GetSnapshot())
                        .ConfigureAwait(false);
                    return;
                case StableDiffusionCppSourceBuildStartOutcome.InsufficientDisk:
                    await BlockAsync(
                            "prerequisites",
                            "There is not enough free disk space to build the image runtime.",
                            result.Activity ?? activityGate.GetSnapshot())
                        .ConfigureAwait(false);
                    return;
                case StableDiffusionCppSourceBuildStartOutcome.MissingPrerequisites:
                    await BlockAsync("prerequisites",
                            "One or more build prerequisites are missing; resolve the checklist before building.",
                            result.Activity ?? activityGate.GetSnapshot())
                        .ConfigureAwait(false);
                    return;
                case StableDiffusionCppSourceBuildStartOutcome.RuntimeBusy:
                    await BlockAsync("runtime-busy",
                            "Wait for active image jobs and image-runtime processes to finish before starting the build.",
                            result.Activity ?? activityGate.GetSnapshot())
                        .ConfigureAwait(false);
                    return;
                case StableDiffusionCppSourceBuildStartOutcome.Started:
                    break;
                default:
                    throw new InvalidOperationException($"Unknown stable-diffusion.cpp source-build start outcome: {result.Outcome}.");
            }

            await Send.OkAsync(new StartStableDiffusionCppSourceBuildResponse
            {
                Started = true,
                Status = buildService.GetStatus().ToResponse()
            }, ct).ConfigureAwait(false);
        }
        catch (StableDiffusionRuntimeException exception)
        {
            await BlockAsync("source-build-error", exception.Message, activityGate.GetSnapshot()).ConfigureAwait(false);
        }
    }

    private Task BlockAsync(string reason, string message, ImageRuntimeActivitySnapshot activity)
    {
        return Send.ResultAsync(Results.Conflict(new ImageRuntimeBlockedResponse
        {
            Reason = reason,
            Message = message,
            Activity = activity.ToResponse()
        }));
    }
}
