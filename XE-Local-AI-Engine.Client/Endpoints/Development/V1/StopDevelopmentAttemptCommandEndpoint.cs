namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;

/// <summary>Stops one running sandbox command of a live attempt; the attempt continues and records the command as not completed.</summary>
public sealed class StopDevelopmentAttemptCommandEndpoint : Endpoint<DevelopmentAttemptCommandRequest>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service;
    private readonly IDevelopmentAttemptLiveBroker _broker;

    public StopDevelopmentAttemptCommandEndpoint(IDevelopmentManagementService service, IDevelopmentAttemptLiveBroker broker)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(broker);
        _service = service;
        _broker = broker;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Development.StopAttemptCommand);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(description => description.Accepts<DevelopmentAttemptCommandRequest>()
                                              .Produces(StatusCodes.Status202Accepted)
                                              .Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(DevelopmentAttemptCommandRequest req, CancellationToken ct)
    {
        // A missing project or task throws DevelopmentNotFoundException (404); the broker then answers only for a command
        // registered under this attempt, so an execution id of another attempt, or one already finished, is 404 too.
        var task = await _service.GetTaskAsync(req.ProjectId, req.TaskId, ct);
        if (task.Attempts.Any(attempt => attempt.Id == req.AttemptId)
            && await _broker.TryStopCommandAsync(req.AttemptId, req.ExecutionId, ct))
        {
            await Send.ResultAsync(TypedResults.Accepted((string?)null));
            return;
        }

        await Send.NotFoundAsync(ct);
    }
}
