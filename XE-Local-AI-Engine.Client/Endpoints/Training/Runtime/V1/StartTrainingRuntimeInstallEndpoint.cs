namespace XE_Local_AI_Engine.Client.Endpoints.Training.Runtime.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.Runtime.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Providers.Training;
using XE_Local_AI_Engine.Providers.Training.Contracts;

public sealed class StartTrainingRuntimeInstallEndpoint(ITrainingRuntimeService runtimeService)
    : EndpointWithoutRequest<StartTrainingRuntimeInstallResponse>
{
    public override void Configure()
    {
        Post(LocalApiRoutes.Training.RuntimeInstall);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<StartTrainingRuntimeInstallResponse>(StatusCodes.Status200OK)
                               .Produces<TrainingRuntimeBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
        {
            await BlockAsync("not-linux", "The Python training runtime is available on Linux only.", prerequisites: null).ConfigureAwait(false);
            return;
        }

        try
        {
            var result = await runtimeService.InstallAsync(ct).ConfigureAwait(false);
            switch (result.Outcome)
            {
                case TrainingRuntimeInstallOutcome.AlreadyRunning:
                    await BlockAsync("already-installing", "A training runtime install is already in progress.", prerequisites: null).ConfigureAwait(false);
                    return;
                case TrainingRuntimeInstallOutcome.InsufficientDisk:
                    await BlockAsync("disk",
                            "There is not enough free disk space to install the training runtime.",
                            result.Prerequisites?.ToResponse())
                        .ConfigureAwait(false);
                    return;
                case TrainingRuntimeInstallOutcome.MissingPrerequisites:
                    await BlockAsync("prerequisites",
                            "One or more training runtime prerequisites are missing; resolve the checklist before installing.",
                            result.Prerequisites?.ToResponse())
                        .ConfigureAwait(false);
                    return;
                case TrainingRuntimeInstallOutcome.Started:
                    break;
                default:
                    throw new InvalidOperationException($"Unknown training runtime install outcome: {result.Outcome}.");
            }

            await Send.OkAsync(new StartTrainingRuntimeInstallResponse
            {
                Started = true,
                Status = runtimeService.GetStatus().ToResponse()
            }, ct).ConfigureAwait(false);
        }
        catch (TrainingRuntimeException exception)
        {
            // TrainingRuntimeException messages are user-safe by contract, so this surfaces verbatim.
            await BlockAsync("prerequisites", exception.Message, prerequisites: null).ConfigureAwait(false);
        }
    }

    private Task BlockAsync(string reason, string message, TrainingRuntimePrerequisitesResponse? prerequisites)
    {
        return Send.ResultAsync(Results.Conflict(new TrainingRuntimeBlockedResponse
        {
            Reason = reason,
            Message = message,
            Prerequisites = prerequisites
        }));
    }
}
