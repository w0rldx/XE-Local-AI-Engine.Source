namespace XE_Local_AI_Engine.Client.Endpoints.Training.Runtime.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.Runtime.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Runtime;
using XE_Local_AI_Engine.Providers.Training;
using XE_Local_AI_Engine.Providers.Training.Contracts;

public sealed class StartTrainingRuntimeInstallEndpoint : EndpointWithoutRequest<StartTrainingRuntimeInstallResponse>
{
    private readonly TrainingRuntimeOrchestrationService _runtime;

    public StartTrainingRuntimeInstallEndpoint(TrainingRuntimeOrchestrationService runtime)
    {
        _runtime = runtime;
    }

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
            await BlockAsync("not-linux", "The Python training runtime is available on Linux only.", prerequisites: null);
            return;
        }

        try
        {
            var result = await _runtime.InstallAsync(ct);
            switch (result.Outcome)
            {
                case TrainingRuntimeInstallOutcome.AlreadyRunning:
                    await BlockAsync(TrainingRuntimeBlockedEndpointSupport.AlreadyInstallingReason,
                        "A training runtime install is already in progress.",
                        prerequisites: null);
                    return;
                case TrainingRuntimeInstallOutcome.InsufficientDisk:
                    await BlockAsync("disk",
                        "There is not enough free disk space to install the training runtime.",
                        result.Prerequisites?.ToResponse());
                    return;
                case TrainingRuntimeInstallOutcome.MissingPrerequisites:
                    await BlockAsync("prerequisites",
                        "One or more training runtime prerequisites are missing; resolve the checklist before installing.",
                        result.Prerequisites?.ToResponse());
                    return;
                case TrainingRuntimeInstallOutcome.Started:
                    break;
                default:
                    throw new InvalidOperationException($"Unknown training runtime install outcome: {result.Outcome}.");
            }

            await Send.OkAsync(new StartTrainingRuntimeInstallResponse
            {
                Started = true,
                Status = _runtime.GetStatus().ToResponse()
            }, ct);
        }
        catch (TrainingRuntimeException exception)
        {
            // TrainingRuntimeException messages are user-safe by contract, so this surfaces verbatim.
            await BlockAsync("prerequisites", exception.Message, prerequisites: null);
        }
    }

    private Task BlockAsync(string reason, string message, TrainingRuntimePrerequisitesResponse? prerequisites) =>
        Send.ResultAsync(TrainingRuntimeBlockedEndpointSupport.Blocked(reason, message, prerequisites));
}
