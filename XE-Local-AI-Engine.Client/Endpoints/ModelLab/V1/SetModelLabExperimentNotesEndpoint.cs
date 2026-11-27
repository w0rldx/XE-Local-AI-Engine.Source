namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>Replaces the operator's note on a finished experiment; a null or blank note clears it.</summary>
public sealed class SetModelLabExperimentNotesEndpoint : Endpoint<SetModelLabExperimentNotesRequest>
{
    private readonly ModelLabExperimentReader _experiments;

    public SetModelLabExperimentNotesEndpoint(ModelLabExperimentReader experiments)
    {
        ArgumentNullException.ThrowIfNull(experiments);
        _experiments = experiments;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.ModelLab.ExperimentNotes);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces(StatusCodes.Status204NoContent)
                               .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                               .Produces(StatusCodes.Status404NotFound)
                               .Produces<ModelLabStartBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(SetModelLabExperimentNotesRequest request, CancellationToken ct)
    {
        switch (await _experiments.SetNotesAsync(request.ExperimentId, request.Notes, ct))
        {
            case ModelLabExperimentUpdateOutcome.Updated:
                await Send.NoContentAsync(ct);
                break;
            case ModelLabExperimentUpdateOutcome.NotFound:
                await Send.NotFoundAsync(ct);
                break;
            default:
                await Send.ResultAsync(ModelLabBlockedEndpointSupport.StillRunning());
                break;
        }
    }
}
