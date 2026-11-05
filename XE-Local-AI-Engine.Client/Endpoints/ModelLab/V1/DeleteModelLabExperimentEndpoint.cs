namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelLab;

public sealed class DeleteModelLabExperimentEndpoint : Endpoint<ModelLabExperimentRouteRequest>
{
    private readonly ModelLabExperimentService _experiments;

    public DeleteModelLabExperimentEndpoint(ModelLabExperimentService experiments)
    {
        ArgumentNullException.ThrowIfNull(experiments);
        _experiments = experiments;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.ModelLab.Experiment);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces(StatusCodes.Status204NoContent)
                               .Produces(StatusCodes.Status404NotFound)
                               .Produces<ModelLabStartBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(ModelLabExperimentRouteRequest request, CancellationToken ct)
    {
        switch (await _experiments.DeleteAsync(request.ExperimentId, ct))
        {
            case ModelLabExperimentDeleteOutcome.Deleted:
                await Send.NoContentAsync(ct);
                break;
            case ModelLabExperimentDeleteOutcome.NotFound:
                await Send.NotFoundAsync(ct);
                break;
            case ModelLabExperimentDeleteOutcome.ProcessNotConfirmedStopped:
                await Send.ResultAsync(TypedResults.Conflict(new ModelLabStartBlockedResponse
                {
                    Reason = ModelLabStartRefusal.ProcessNotConfirmedStopped,
                    Message =
                        "This experiment's process has not been confirmed stopped, and its record is what lets the node find it. The next start of the node checks it again; delete it after that."
                }));
                break;
            default:
                await Send.ResultAsync(TypedResults.Conflict(new ModelLabStartBlockedResponse
                {
                    Reason = ModelLabStartRefusal.Busy,
                    Message = "The experiment is still running. Cancel it first."
                }));
                break;
        }
    }
}
