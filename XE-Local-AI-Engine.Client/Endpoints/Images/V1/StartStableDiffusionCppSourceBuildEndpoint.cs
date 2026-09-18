namespace XE_Local_AI_Engine.Client.Endpoints.Images.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Images.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Images;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp;
using XE_Local_AI_Engine.Providers.StableDiffusionCpp.Contracts;

public sealed class StartStableDiffusionCppSourceBuildEndpoint : Endpoint<StartStableDiffusionCppSourceBuildRequest, StartStableDiffusionCppSourceBuildResponse>
{
    private readonly ImageRuntimeOrchestrationService _imageRuntime;

    public StartStableDiffusionCppSourceBuildEndpoint(ImageRuntimeOrchestrationService imageRuntime)
    {
        _imageRuntime = imageRuntime;
    }

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
            await BlockAsync("not-linux", "In-app source builds are available on Linux only.", _imageRuntime.GetActivitySnapshot());
            return;
        }

        try
        {
            var result = await _imageRuntime.StartAsync(request.ToContract(), ct);
            switch (result.Outcome)
            {
                case StableDiffusionCppSourceBuildStartOutcome.AlreadyRunning:
                    await BlockAsync("already-building",
                            "A stable-diffusion.cpp source build is already in progress.",
                            result.Activity ?? _imageRuntime.GetActivitySnapshot());
                    return;
                case StableDiffusionCppSourceBuildStartOutcome.InsufficientDisk:
                    await BlockAsync("prerequisites",
                            "There is not enough free disk space to build the image runtime.",
                            result.Activity ?? _imageRuntime.GetActivitySnapshot());
                    return;
                case StableDiffusionCppSourceBuildStartOutcome.MissingPrerequisites:
                    await BlockAsync("prerequisites",
                            "One or more build prerequisites are missing; resolve the checklist before building.",
                            result.Activity ?? _imageRuntime.GetActivitySnapshot());
                    return;
                case StableDiffusionCppSourceBuildStartOutcome.RuntimeBusy:
                    await BlockAsync("runtime-busy",
                            "Wait for active image jobs and image-runtime processes to finish before starting the build.",
                            result.Activity ?? _imageRuntime.GetActivitySnapshot());
                    return;
                case StableDiffusionCppSourceBuildStartOutcome.Started:
                    break;
                default:
                    throw new InvalidOperationException($"Unknown stable-diffusion.cpp source-build start outcome: {result.Outcome}.");
            }

            await Send.OkAsync(new StartStableDiffusionCppSourceBuildResponse
            {
                Started = true,
                Status = _imageRuntime.GetStatus().ToResponse()
            }, ct);
        }
        catch (StableDiffusionRuntimeException exception)
        {
            await BlockAsync("source-build-error", exception.Message, _imageRuntime.GetActivitySnapshot());
        }
    }

    private Task BlockAsync(string reason, string message, ImageRuntimeActivitySnapshot activity) =>
        Send.ResultAsync(ImageRuntimeBlockedEndpointSupport.Blocked(reason, message, activity));
}
