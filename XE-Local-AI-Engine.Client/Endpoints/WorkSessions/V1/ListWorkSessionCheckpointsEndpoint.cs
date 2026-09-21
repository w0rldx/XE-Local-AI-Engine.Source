namespace XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

public sealed class ListWorkSessionCheckpointsEndpoint : Endpoint<WorkSessionFeedRequest, ListWorkSessionCheckpointsResponse>
{
    private readonly IWorkSessionService _service;

    public ListWorkSessionCheckpointsEndpoint(IWorkSessionService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.WorkSessions.Checkpoints);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(WorkSessionFeedRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var checkpoints = await _service.ListCheckpointsAsync(req.SessionId, req.SinceSeq, ct);
        await Send.OkAsync(checkpoints.ToResponse(), ct);
    }
}
