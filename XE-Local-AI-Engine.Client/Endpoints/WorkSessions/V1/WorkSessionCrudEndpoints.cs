namespace XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

public sealed class ListWorkSessionsEndpoint : EndpointWithoutRequest<ListWorkSessionsResponse>
{
    private readonly IWorkSessionService _service;

    public ListWorkSessionsEndpoint(IWorkSessionService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.WorkSessions.Root);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var sessions = await _service.ListAsync(ct);
        await Send.OkAsync(new ListWorkSessionsResponse { Items = [.. sessions.Select(WorkSessionContractMapper.ToResponse)] }, ct);
    }
}

public sealed class CreateWorkSessionEndpoint : Endpoint<CreateWorkSessionRequest, WorkSessionResponse>
{
    private readonly IWorkSessionService _service;

    public CreateWorkSessionEndpoint(IWorkSessionService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

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
        var created = await _service.CreateAsync(new CreateWorkSessionRequestModel(req.Title, req.Objective, kind, req.AgentDefinitionId), ct);
        await Send.CreatedAtAsync<GetWorkSessionEndpoint>(new
            {
                sessionId = created.Id
            },
            created.ToResponse(),
            cancellation: ct);
    }
}

public sealed class GetWorkSessionEndpoint : Endpoint<WorkSessionRequest, WorkSessionResponse>
{
    private readonly IWorkSessionService _service;

    public GetWorkSessionEndpoint(IWorkSessionService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.WorkSessions.ById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(WorkSessionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var session = await _service.GetAsync(req.SessionId, ct);
        await Send.OkAsync(session.ToResponse(), ct);
    }
}

public sealed class UpdateWorkSessionEndpoint : Endpoint<UpdateWorkSessionRequest, WorkSessionResponse>
{
    private readonly IWorkSessionService _service;

    public UpdateWorkSessionEndpoint(IWorkSessionService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

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

        // An omitted member is forwarded as null, which the service reads as "leave it alone" — a PATCH that only
        // renames must not blank the objective it never mentioned.
        var updated = await _service.UpdateAsync(req.SessionId,
                                        new UpdateWorkSessionRequestModel(req.Title, req.Objective, req.AgentDefinitionId),
                                        ct);
        await Send.OkAsync(updated.ToResponse(), ct);
    }
}

public sealed class DeleteWorkSessionEndpoint : Endpoint<WorkSessionRequest>
{
    private readonly IWorkSessionService _service;

    public DeleteWorkSessionEndpoint(IWorkSessionService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

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

        await _service.DeleteAsync(req.SessionId, ct);
        await Send.NoContentAsync(ct);
    }
}
