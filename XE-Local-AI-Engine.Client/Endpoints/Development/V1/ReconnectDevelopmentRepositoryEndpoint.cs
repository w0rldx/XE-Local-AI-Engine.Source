namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class ReconnectDevelopmentRepositoryEndpoint : Endpoint<ReconnectDevelopmentRepositoryRequest, DevelopmentProjectDetailResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service;

    public ReconnectDevelopmentRepositoryEndpoint(IDevelopmentManagementService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.Development.RepositoryConnection);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(ReconnectDevelopmentRepositoryRequest req, CancellationToken ct)
    {
        try
        {
            var project = await _service.ReconnectRepositoryAsync(req.ProjectId, req.SelectedFolderId, req.ExpectedVersion, ct);
            await Send.OkAsync(project.ToResponse(), ct);
        }
        // Reconnect is the one Development endpoint whose request BOTH carries a folder to validate and acts on the project's persisted binding, so it alone splits
        // the workspace-security family by type: a persisted binding blocking it is a 409, an unusable picked folder (not a Git root, read-only, network path) a 400.
        catch (DevelopmentRepositoryStateConflictException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(statusCode: StatusCodes.Status409Conflict, cancellation: ct);
        }
        catch (DevelopmentWorkspaceSecurityException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct);
        }
    }
}
