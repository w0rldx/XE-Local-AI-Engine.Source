namespace XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

public sealed class ListWorkSessionEventsEndpoint : Endpoint<WorkSessionEventFeedRequest, ListWorkSessionEventsResponse>
{
    private readonly IWorkSessionService _service;

    public ListWorkSessionEventsEndpoint(IWorkSessionService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.WorkSessions.Events);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(WorkSessionEventFeedRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var events = await _service.ListEventsAsync(req.SessionId, req.SinceSeq, req.Limit, ct);
        await Send.OkAsync(events.ToResponse(req.Limit), ct);
    }
}
