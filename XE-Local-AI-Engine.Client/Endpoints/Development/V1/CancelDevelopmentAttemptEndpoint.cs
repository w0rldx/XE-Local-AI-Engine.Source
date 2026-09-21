namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class CancelDevelopmentAttemptEndpoint : Endpoint<DevelopmentAttemptRequest>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service;

    public CancelDevelopmentAttemptEndpoint(IDevelopmentManagementService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Development.CancelAttempt);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(description => description.Accepts<DevelopmentAttemptRequest>());
    }

    public override async Task HandleAsync(DevelopmentAttemptRequest req, CancellationToken ct)
    {
        // Cancelling an attempt that had already finished is not an error — both outcomes are 204. A missing
        // project/task/attempt throws DevelopmentNotFoundException, which the global handler answers 404.
        _ = await _service.CancelAttemptAsync(req.ProjectId, req.TaskId, req.AttemptId, ct);
        await Send.NoContentAsync(ct);
    }
}
