namespace XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1;

using System.Text.Json;
using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.ModelLab.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.ModelLab;

/// <summary>200 with the run and its trace; an unknown run is 404; a trace that cannot be read is a 409 <c>TraceUnavailable</c>, and the run stays deletable.</summary>
public sealed class GetModelLabPlaygroundRunEndpoint : Endpoint<ModelLabPlaygroundRunRouteRequest, ModelLabPlaygroundRunDetailResponse>
{
    private readonly ModelLabPlaygroundService _playground;

    public GetModelLabPlaygroundRunEndpoint(ModelLabPlaygroundService playground)
    {
        ArgumentNullException.ThrowIfNull(playground);
        _playground = playground;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.ModelLab.PlaygroundRun);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder
                               .Produces<ModelLabPlaygroundRunDetailResponse>(StatusCodes.Status200OK)
                               .Produces(StatusCodes.Status404NotFound)
                               .Produces<ModelLabPlaygroundBlockedResponse>(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(ModelLabPlaygroundRunRouteRequest req, CancellationToken ct)
    {
        var read = await _playground.GetAsync(req.RunId, ct);
        if (read.Trace is not { } trace)
        {
            await Send.ResultAsync(TypedResults.Conflict(ModelLabPlaygroundMapper.Blocked(ModelLabPlaygroundRefusal.TraceUnavailable,
                read.UnavailableMessage ?? "The saved trace of this run cannot be read.")));
            return;
        }

        using var document = JsonDocument.Parse(trace);
        await Send.OkAsync(new ModelLabPlaygroundRunDetailResponse
        {
            Run = ModelLabPlaygroundMapper.ToResponse(read.Run),
            Trace = document.RootElement.Clone()
        }, ct);
    }
}
