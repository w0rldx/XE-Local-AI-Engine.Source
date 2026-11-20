namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>Ends an Idle warm Model Lab worker: 204 once none is left, the 409 <c>Busy</c> while a job holds it, or <c>OrphanWorker</c> when its exit was not seen.</summary>
public sealed class EjectModelLabWarmWorkerEndpoint : EndpointWithoutRequest
{
    private readonly IModelLabWarmResidentSource _warm;

    public EjectModelLabWarmWorkerEndpoint(IModelLabWarmResidentSource warm)
    {
        ArgumentNullException.ThrowIfNull(warm);
        _warm = warm;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelLab.WarmEject);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces(StatusCodes.Status204NoContent)
                               .Produces<ModelLabStartBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        switch (await _warm.EjectAsync(ct))
        {
            case ModelLabEjectOutcome.Gone:
                await Send.NoContentAsync(ct);
                break;
            case ModelLabEjectOutcome.Busy:
                await Send.ResultAsync(ModelLabBlockedEndpointSupport.Blocked(ModelLabStartRefusal.Busy,
                    "A Model Lab job is using the warm worker. Wait for it to finish or cancel it, then eject."));
                break;
            default:
                // The same condition that refuses starts, so it carries the same reason.
                await Send.ResultAsync(ModelLabBlockedEndpointSupport.Blocked(ModelLabStartRefusal.OrphanWorker,
                    "The Model Lab worker was stopped, but its exit was not confirmed, so it may still hold GPU memory. New Model Lab starts are refused until it is gone. Stop that process if it still runs, then eject again."));
                break;
        }
    }
}
