namespace XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

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

        var session = await _service.GetAsync(req.SessionId, ct).ConfigureAwait(false);
        await Send.OkAsync(session.ToResponse(), ct).ConfigureAwait(false);
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

        // An omitted member is forwarded as null, which the service reads as "leave it alone" — a PATCH that only
        // renames must not blank the objective it never mentioned.
        var updated = await _service.UpdateAsync(req.SessionId,
                                        new UpdateWorkSessionRequestModel(req.Title, req.Objective, req.AgentDefinitionId),
                                        ct)
                                    .ConfigureAwait(false);
        await Send.OkAsync(updated.ToResponse(), ct).ConfigureAwait(false);
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

        await _service.DeleteAsync(req.SessionId, ct).ConfigureAwait(false);
        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}
