namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class StartDevelopmentNextActionEndpoint : Endpoint<DevelopmentActionRequest, DevelopmentNextActionResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service;

    public StartDevelopmentNextActionEndpoint(IDevelopmentManagementService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

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
            var result = await _service.StartNextActionAsync(req.ProjectId, req.TaskId, req.OperationId, ct);
            await Send.OkAsync(new DevelopmentNextActionResponse
                {
                    Action = result.Action,
                    ProjectId = result.ProjectId,
                    TaskId = result.TaskId,
                    AttemptId = result.AttemptId,
                    TaskStatus = result.TaskStatus.ToString(),
                    Role = result.Role?.ToString()
                },
                ct);
        }
        catch (DevelopmentWorkspaceSecurityException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(statusCode: StatusCodes.Status409Conflict, cancellation: ct);
        }
    }
}
