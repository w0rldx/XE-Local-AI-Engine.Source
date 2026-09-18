namespace XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

/// <summary>
///     Starts a session. 202, not 200: the status moves here, but the step that follows is the supervisor's, taken out
///     of band on the node's one invocation slot — accepted is the honest answer, started is not.
/// </summary>
public sealed class StartWorkSessionEndpoint : Endpoint<WorkSessionRequest, WorkSessionResponse>
{
    private readonly IWorkSessionService _service;

    public StartWorkSessionEndpoint(IWorkSessionService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.WorkSessions.Start);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<WorkSessionResponse>(StatusCodes.Status202Accepted)
                                      .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(WorkSessionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var started = await _service.StartAsync(req.SessionId, ct);
        await Send.ResultAsync(Results.Accepted(value: started.ToResponse()));
    }
}

public sealed class PauseWorkSessionEndpoint : Endpoint<WorkSessionRequest, WorkSessionResponse>
{
    private readonly IWorkSessionService _service;

    public PauseWorkSessionEndpoint(IWorkSessionService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.WorkSessions.Pause);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(WorkSessionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var paused = await _service.PauseAsync(req.SessionId, ct);
        await Send.OkAsync(paused.ToResponse(), ct);
    }
}

public sealed class ResumeWorkSessionEndpoint : Endpoint<WorkSessionRequest, WorkSessionResponse>
{
    private readonly IWorkSessionService _service;

    public ResumeWorkSessionEndpoint(IWorkSessionService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.WorkSessions.Resume);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<WorkSessionResponse>(StatusCodes.Status202Accepted)
                                      .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(WorkSessionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var resumed = await _service.ResumeAsync(req.SessionId, ct);
        await Send.ResultAsync(Results.Accepted(value: resumed.ToResponse()));
    }
}

public sealed class CancelWorkSessionEndpoint : Endpoint<WorkSessionRequest, WorkSessionResponse>
{
    private readonly IWorkSessionService _service;

    public CancelWorkSessionEndpoint(IWorkSessionService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.WorkSessions.Cancel);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(WorkSessionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var cancelled = await _service.CancelAsync(req.SessionId, ct);
        await Send.OkAsync(cancelled.ToResponse(), ct);
    }
}
