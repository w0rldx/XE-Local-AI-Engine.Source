namespace XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.WorkSessions.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.WorkSessions;

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
