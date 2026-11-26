namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>204 once the run and its trace scope are gone; an unknown run is 404.</summary>
public sealed class DeleteModelLabPlaygroundRunEndpoint : Endpoint<ModelLabPlaygroundRunRouteRequest>
{
    private readonly ModelLabPlaygroundService _playground;

    public DeleteModelLabPlaygroundRunEndpoint(ModelLabPlaygroundService playground)
    {
        ArgumentNullException.ThrowIfNull(playground);
        _playground = playground;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.ModelLab.PlaygroundRun);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces(StatusCodes.Status204NoContent)
                               .Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(ModelLabPlaygroundRunRouteRequest req, CancellationToken ct)
    {
        await _playground.DeleteAsync(req.RunId, ct);
        await Send.NoContentAsync(ct);
    }
}
