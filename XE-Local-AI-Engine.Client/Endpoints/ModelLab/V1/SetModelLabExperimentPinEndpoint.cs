namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using System.Globalization;
using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>Pins or unpins a finished experiment. A pinned one survives retention's trim; a manual delete still removes it.</summary>
public sealed class SetModelLabExperimentPinEndpoint : Endpoint<SetModelLabExperimentPinRequest>
{
    private readonly ModelLabExperimentReader _experiments;

    public SetModelLabExperimentPinEndpoint(ModelLabExperimentReader experiments)
    {
        ArgumentNullException.ThrowIfNull(experiments);
        _experiments = experiments;
    }

    public override void Configure()
    {
        Put(LocalApiRoutes.ModelLab.ExperimentPin);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces(StatusCodes.Status204NoContent)
                               .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                               .Produces(StatusCodes.Status404NotFound)
                               .Produces<ModelLabStartBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(SetModelLabExperimentPinRequest request, CancellationToken ct)
    {
        switch (await _experiments.SetPinnedAsync(request.ExperimentId, request.Pinned, ct))
        {
            case ModelLabExperimentUpdateOutcome.Updated:
                await Send.NoContentAsync(ct);
                break;
            case ModelLabExperimentUpdateOutcome.NotFound:
                await Send.NotFoundAsync(ct);
                break;
            case ModelLabExperimentUpdateOutcome.PinLimitReached:
                await Send.ResultAsync(ModelLabBlockedEndpointSupport.Blocked(ModelLabStartRefusal.PinLimitReached, string.Create(CultureInfo.InvariantCulture,
                    $"At most {ModelLabExperimentService.MaxPinnedExperiments} experiments can be pinned. Unpin one first.")));
                break;
            default:
                await Send.ResultAsync(ModelLabBlockedEndpointSupport.StillRunning());
                break;
        }
    }
}
