namespace XE_Local_AI_Engine.Client.Endpoints.Training.Runtime.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Training.Runtime.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Training.Runtime;
using XE_Local_AI_Engine.Providers.Training;

/// <summary>
///     Removes the installed training runtime. Also the cancel verb for an install in flight: cancelling first and then
///     removing is what an operator means by "stop and undo this", and a separate cancel route would only let the two
///     get out of step.
/// </summary>
public sealed class RemoveTrainingRuntimeEndpoint : EndpointWithoutRequest<TrainingRuntimeStatusResponse>
{
    private readonly TrainingRuntimeOrchestrationService _runtime;

    public RemoveTrainingRuntimeEndpoint(TrainingRuntimeOrchestrationService runtime)
    {
        _runtime = runtime;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Training.RuntimeRemove);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<TrainingRuntimeStatusResponse>(StatusCodes.Status200OK)
                               .Produces<TrainingRuntimeBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        if (_runtime.Cancel())
        {
            // The install tears its own tree down on cancellation; reporting the in-progress status back is honest,
            // and the client polls or listens on the hub for the terminal transition.
            await Send.OkAsync(_runtime.GetStatus().ToResponse(), ct);
            return;
        }

        try
        {
            if (!await _runtime.RemoveAsync(ct))
            {
                await Send.ResultAsync(TrainingRuntimeBlockedEndpointSupport.Blocked(TrainingRuntimeBlockedEndpointSupport.AlreadyInstallingReason,
                              "A training runtime install is in progress. Cancel it before removing the runtime."));
                return;
            }
        }
        catch (TrainingRuntimeException exception)
        {
            await Send.ResultAsync(TrainingRuntimeBlockedEndpointSupport.Blocked("remove-failed", exception.Message));
            return;
        }

        await Send.OkAsync(_runtime.GetStatus().ToResponse(), ct);
    }
}
