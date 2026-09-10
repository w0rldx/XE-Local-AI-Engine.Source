namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class GetDevelopmentTaskEndpoint(IDevelopmentManagementService service)
    : Endpoint<DevelopmentTaskRequest, DevelopmentTaskDetailResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.Development.TaskById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DevelopmentTaskRequest req, CancellationToken ct)
    {
        await Send.OkAsync((await _service.GetTaskAsync(req.ProjectId, req.TaskId, ct).ConfigureAwait(false)).ToResponse(), ct).ConfigureAwait(false);
    }
}

public sealed class StartDevelopmentNextActionEndpoint(IDevelopmentManagementService service)
    : Endpoint<DevelopmentActionRequest, DevelopmentNextActionResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Post(LocalApiRoutes.Development.NextAction);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(DevelopmentActionRequest req, CancellationToken ct)
    {
        try
        {
            var result = await _service.StartNextActionAsync(req.ProjectId, req.TaskId, req.OperationId, ct).ConfigureAwait(false);
            await Send.OkAsync(new DevelopmentNextActionResponse(result.Action,
                    result.ProjectId,
                    result.TaskId,
                    result.AttemptId,
                    result.TaskStatus.ToString(),
                    result.Role?.ToString()),
                ct).ConfigureAwait(false);
        }
        catch (DevelopmentWorkspaceSecurityException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(statusCode: StatusCodes.Status409Conflict, cancellation: ct).ConfigureAwait(false);
        }
    }
}

public sealed class CancelDevelopmentAttemptEndpoint(IDevelopmentManagementService service)
    : Endpoint<DevelopmentAttemptRequest>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service = service ?? throw new ArgumentNullException(nameof(service));

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
        _ = await _service.CancelAttemptAsync(req.ProjectId, req.TaskId, req.AttemptId, ct).ConfigureAwait(false);
        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}
