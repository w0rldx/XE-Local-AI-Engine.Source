namespace XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

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
                                        new UpdateWorkSessionRequestModel { Title = req.Title, Objective = req.Objective, AgentDefinitionId = req.AgentDefinitionId },
                                        ct);
        await Send.OkAsync(updated.ToResponse(), ct);
    }
}
