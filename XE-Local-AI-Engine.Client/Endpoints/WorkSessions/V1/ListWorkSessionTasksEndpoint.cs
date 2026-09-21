namespace XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

// Incremental feeds use ?sinceSeq= so a hub notification refreshes only data after the caller's watermark.

public sealed class ListWorkSessionTasksEndpoint : Endpoint<WorkSessionFeedRequest, ListWorkSessionTasksResponse>
{
    private readonly IWorkSessionService _service;

    public ListWorkSessionTasksEndpoint(IWorkSessionService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.WorkSessions.Tasks);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(WorkSessionFeedRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var tasks = await _service.ListTasksAsync(req.SessionId, req.SinceSeq, ct);
        await Send.OkAsync(tasks.ToResponse(), ct);
    }
}
