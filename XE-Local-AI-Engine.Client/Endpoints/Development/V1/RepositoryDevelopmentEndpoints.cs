namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class ListDevelopmentRepositoriesEndpoint(IDevelopmentManagementService service)
    : EndpointWithoutRequest<ListDevelopmentRepositoriesResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.Development.Repositories);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var repositories = await _service.ListRepositoriesAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ListDevelopmentRepositoriesResponse(repositories.Select(DevelopmentContractMapper.ToResponse).ToArray()), ct)
                  .ConfigureAwait(false);
    }
}

public sealed class RegisterDevelopmentRepositoryEndpoint(IDevelopmentManagementService service)
    : Endpoint<RegisterDevelopmentRepositoryRequest, DevelopmentRepositoryResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Post(LocalApiRoutes.Development.Repositories);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(RegisterDevelopmentRepositoryRequest req, CancellationToken ct)
    {
        try
        {
            var repository = await _service.RegisterRepositoryAsync(req.Alias, req.HostPath, ct).ConfigureAwait(false);
            await Send.OkAsync(repository.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (SelectedFolderEndpointSupport.IsHandled(exception))
        {
            await SelectedFolderEndpointSupport.SendAsync(this, Send, exception, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or DevelopmentWorkspaceSecurityException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}

public sealed class DetectDevelopmentRepositoryProfileEndpoint(IDevelopmentManagementService service)
    : Endpoint<DevelopmentProfileDetectionRequest, DevelopmentProfileDetectionResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.Development.RepositoryProfileDetection);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(DevelopmentProfileDetectionRequest req, CancellationToken ct)
    {
        try
        {
            var detection = await _service.DetectRepositoryProfileAsync(req.SelectedFolderId, ct).ConfigureAwait(false);
            await Send.OkAsync(new DevelopmentProfileDetectionResponse(detection.ProfileId, detection.BuildTarget, detection.Candidates), ct)
                      .ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (SelectedFolderEndpointSupport.IsHandled(exception))
        {
            await SelectedFolderEndpointSupport.SendAsync(this, Send, exception, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is DevelopmentWorkspaceSecurityException or DirectoryNotFoundException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}

public sealed class ReconnectDevelopmentRepositoryEndpoint(IDevelopmentManagementService service)
    : Endpoint<ReconnectDevelopmentRepositoryRequest, DevelopmentProjectDetailResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service = service ?? throw new ArgumentNullException(nameof(service));

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
            var project = await _service.ReconnectRepositoryAsync(req.ProjectId, req.SelectedFolderId, req.ExpectedVersion, ct)
                                        .ConfigureAwait(false);
            await Send.OkAsync(project.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (SelectedFolderEndpointSupport.IsHandled(exception))
        {
            await SelectedFolderEndpointSupport.SendAsync(this, Send, exception, ct).ConfigureAwait(false);
        }
        // Reconnect is the one Development endpoint whose request BOTH carries a folder to validate and acts on the
        // project's persisted binding, so it is the only one that has to split the workspace-security family by type:
        // the persisted binding blocking the reconnect is a 409, while the folder the caller just picked being
        // unusable (not a Git root, read-only, network path) is the same 400 it is on register/create.
        catch (Exception exception) when (exception is DevelopmentConcurrencyException
                                              or DevelopmentRepositoryStateConflictException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(statusCode: StatusCodes.Status409Conflict, cancellation: ct).ConfigureAwait(false);
        }
        catch (DevelopmentWorkspaceSecurityException exception)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
