namespace XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

// The whole area answers 404 rather than 500 when WorkSessions:Enabled is false, from the request-path middleware in
// Program that runs ahead of authentication — the same shape Development Mode uses. The routes stay mapped either way,
// so the OpenAPI document (and therefore the generated client) describes the surface regardless of the node's switch.
//
// Errors are never hand-built here: KeyNotFoundException is the one caught locally (404, no body), while the two
// Persistence conflict types and the service's validation exception reach ConflictExceptionHandler and
// DomainValidationExceptionHandler, which own the 409 and 400 envelopes for the whole node.

public sealed class ListWorkSessionsEndpoint(IWorkSessionService service) : EndpointWithoutRequest<ListWorkSessionsResponse>
{
    private readonly IWorkSessionService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.WorkSessions.Root);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var sessions = await _service.ListAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ListWorkSessionsResponse([.. sessions.Select(WorkSessionContractMapper.ToResponse)]), ct).ConfigureAwait(false);
    }
}

public sealed class CreateWorkSessionEndpoint(IWorkSessionService service) : Endpoint<CreateWorkSessionRequest, WorkSessionResponse>
{
    private readonly IWorkSessionService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Post(LocalApiRoutes.WorkSessions.Root);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(CreateWorkSessionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // Safe to parse rather than TryParse: the validator has already refused anything that is not General or
        // Research, and it runs before the handler.
        var kind = Enum.Parse<AgentWorkSessionKind>(req.Kind, ignoreCase: true);
        var created = await _service.CreateAsync(new CreateWorkSessionRequestModel(req.Title, req.Objective, kind, req.AgentDefinitionId), ct)
                                    .ConfigureAwait(false);
        await Send.CreatedAtAsync<GetWorkSessionEndpoint>(new
            {
                sessionId = created.Id
            },
            created.ToResponse(),
            cancellation: ct).ConfigureAwait(false);
    }
}

public sealed class GetWorkSessionEndpoint(IWorkSessionService service) : Endpoint<WorkSessionRequest, WorkSessionResponse>
{
    private readonly IWorkSessionService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.WorkSessions.ById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(WorkSessionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        try
        {
            var session = await _service.GetAsync(req.SessionId, ct).ConfigureAwait(false);
            await Send.OkAsync(session.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }
}

public sealed class UpdateWorkSessionEndpoint(IWorkSessionService service) : Endpoint<UpdateWorkSessionRequest, WorkSessionResponse>
{
    private readonly IWorkSessionService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Patch(LocalApiRoutes.WorkSessions.ById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(UpdateWorkSessionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        try
        {
            // An omitted member is forwarded as null, which the service reads as "leave it alone" — a PATCH that only
            // renames must not blank the objective it never mentioned.
            var updated = await _service.UpdateAsync(req.SessionId,
                                            new UpdateWorkSessionRequestModel(req.Title, req.Objective, req.AgentDefinitionId),
                                            ct)
                                        .ConfigureAwait(false);
            await Send.OkAsync(updated.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }
}

public sealed class DeleteWorkSessionEndpoint(IWorkSessionService service) : Endpoint<WorkSessionRequest>
{
    private readonly IWorkSessionService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Delete(LocalApiRoutes.WorkSessions.ById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status204NoContent)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(WorkSessionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        try
        {
            await _service.DeleteAsync(req.SessionId, ct).ConfigureAwait(false);
            await Send.NoContentAsync(ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }
}

/// <summary>
///     Starts a session. 202, not 200: the status moves here, but the step that follows is the supervisor's, taken out
///     of band on the node's one invocation slot — accepted is the honest answer, started is not.
/// </summary>
public sealed class StartWorkSessionEndpoint(IWorkSessionService service) : Endpoint<WorkSessionRequest, WorkSessionResponse>
{
    private readonly IWorkSessionService _service = service ?? throw new ArgumentNullException(nameof(service));

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

        try
        {
            var started = await _service.StartAsync(req.SessionId, ct).ConfigureAwait(false);
            await Send.ResultAsync(Results.Accepted(value: started.ToResponse())).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }
}

public sealed class PauseWorkSessionEndpoint(IWorkSessionService service) : Endpoint<WorkSessionRequest, WorkSessionResponse>
{
    private readonly IWorkSessionService _service = service ?? throw new ArgumentNullException(nameof(service));

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

        try
        {
            var paused = await _service.PauseAsync(req.SessionId, ct).ConfigureAwait(false);
            await Send.OkAsync(paused.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }
}

public sealed class ResumeWorkSessionEndpoint(IWorkSessionService service) : Endpoint<WorkSessionRequest, WorkSessionResponse>
{
    private readonly IWorkSessionService _service = service ?? throw new ArgumentNullException(nameof(service));

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

        try
        {
            var resumed = await _service.ResumeAsync(req.SessionId, ct).ConfigureAwait(false);
            await Send.ResultAsync(Results.Accepted(value: resumed.ToResponse())).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }
}

public sealed class CancelWorkSessionEndpoint(IWorkSessionService service) : Endpoint<WorkSessionRequest, WorkSessionResponse>
{
    private readonly IWorkSessionService _service = service ?? throw new ArgumentNullException(nameof(service));

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

        try
        {
            var cancelled = await _service.CancelAsync(req.SessionId, ct).ConfigureAwait(false);
            await Send.OkAsync(cancelled.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }
}
