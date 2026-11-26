namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>Trains one run inside the request and answers 200 with the saved run; a refusal or a failed run saves nothing and is a 409 with a stable reason.</summary>
public sealed class StartModelLabPlaygroundRunEndpoint : Endpoint<StartModelLabPlaygroundRunRequest, ModelLabPlaygroundRunResponse>
{
    private readonly ModelLabPlaygroundService _playground;

    public StartModelLabPlaygroundRunEndpoint(ModelLabPlaygroundService playground)
    {
        ArgumentNullException.ThrowIfNull(playground);
        _playground = playground;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.ModelLab.PlaygroundRuns);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<ModelLabPlaygroundRunResponse>(StatusCodes.Status200OK)
                               .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                               .Produces<ModelLabPlaygroundBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(StartModelLabPlaygroundRunRequest req, CancellationToken ct)
    {
        var outcome = await _playground.StartAsync(ModelLabPlaygroundMapper.ToConfig(req), ct);
        if (outcome.Run is { } run)
        {
            await Send.OkAsync(ModelLabPlaygroundMapper.ToResponse(run), ct);
            return;
        }

        await Send.ResultAsync(TypedResults.Conflict(ModelLabPlaygroundMapper.Blocked(outcome.Refusal ?? ModelLabPlaygroundRefusal.RunFailed,
            outcome.RefusalMessage ?? "The run could not be completed.")));
    }
}
