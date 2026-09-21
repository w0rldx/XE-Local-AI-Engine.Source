namespace XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

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
        var created = await _service.CreateAsync(new CreateWorkSessionRequestModel { Title = req.Title, Objective = req.Objective, Kind = kind, AgentDefinitionId = req.AgentDefinitionId }, ct);
        await Send.CreatedAtAsync<GetWorkSessionEndpoint>(new
            {
                sessionId = created.Id
            },
            created.ToResponse(),
            cancellation: ct);
    }
}
