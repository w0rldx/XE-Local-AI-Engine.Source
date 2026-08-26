namespace XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

// Incremental feeds use ?sinceSeq= so a hub notification refreshes only data after the caller's watermark.

public sealed class ListWorkSessionTasksEndpoint(IWorkSessionService service) : Endpoint<WorkSessionFeedRequest, ListWorkSessionTasksResponse>
{
    private readonly IWorkSessionService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.WorkSessions.Tasks);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(WorkSessionFeedRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var tasks = await _service.ListTasksAsync(req.SessionId, req.SinceSeq, ct).ConfigureAwait(false);
        await Send.OkAsync(tasks.ToResponse(), ct).ConfigureAwait(false);
    }
}

public sealed class ListWorkSessionFindingsEndpoint(IWorkSessionService service) : Endpoint<WorkSessionFeedRequest, ListWorkSessionFindingsResponse>
{
    private readonly IWorkSessionService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.WorkSessions.Findings);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(WorkSessionFeedRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var findings = await _service.ListFindingsAsync(req.SessionId, req.SinceSeq, ct).ConfigureAwait(false);
        await Send.OkAsync(findings.ToResponse(), ct).ConfigureAwait(false);
    }
}

public sealed class ListWorkSessionArtifactsEndpoint(IWorkSessionService service) : Endpoint<WorkSessionFeedRequest, ListWorkSessionArtifactsResponse>
{
    private readonly IWorkSessionService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.WorkSessions.Artifacts);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(WorkSessionFeedRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var artifacts = await _service.ListArtifactsAsync(req.SessionId, req.SinceSeq, ct).ConfigureAwait(false);
        await Send.OkAsync(artifacts.ToResponse(), ct).ConfigureAwait(false);
    }
}

public sealed class ListWorkSessionCheckpointsEndpoint(IWorkSessionService service)
    : Endpoint<WorkSessionFeedRequest, ListWorkSessionCheckpointsResponse>
{
    private readonly IWorkSessionService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.WorkSessions.Checkpoints);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(WorkSessionFeedRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var checkpoints = await _service.ListCheckpointsAsync(req.SessionId, req.SinceSeq, ct).ConfigureAwait(false);
        await Send.OkAsync(checkpoints.ToResponse(), ct).ConfigureAwait(false);
    }
}

public sealed class ListWorkSessionEventsEndpoint(IWorkSessionService service) : Endpoint<WorkSessionEventFeedRequest, ListWorkSessionEventsResponse>
{
    private readonly IWorkSessionService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.WorkSessions.Events);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(WorkSessionEventFeedRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var events = await _service.ListEventsAsync(req.SessionId, req.SinceSeq, req.Limit, ct).ConfigureAwait(false);
        await Send.OkAsync(events.ToResponse(req.Limit), ct).ConfigureAwait(false);
    }
}
