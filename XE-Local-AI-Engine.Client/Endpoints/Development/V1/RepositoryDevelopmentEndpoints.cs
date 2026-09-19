namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class ListDevelopmentRepositoriesEndpoint : EndpointWithoutRequest<ListDevelopmentRepositoriesResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service;

    public ListDevelopmentRepositoriesEndpoint(IDevelopmentManagementService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Development.Repositories);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var repositories = await _service.ListRepositoriesAsync(ct);
        await Send.OkAsync(new ListDevelopmentRepositoriesResponse { Items = repositories.Select(DevelopmentContractMapper.ToResponse).ToArray() }, ct);
    }
}

public sealed class RegisterDevelopmentRepositoryEndpoint : Endpoint<RegisterDevelopmentRepositoryRequest, DevelopmentRepositoryResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service;

    public RegisterDevelopmentRepositoryEndpoint(IDevelopmentManagementService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

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
            var repository = await _service.RegisterRepositoryAsync(req.Alias, req.HostPath, ct);
            await Send.OkAsync(repository.ToResponse(), ct);
        }
        catch (Exception exception) when (exception is ArgumentException or DevelopmentWorkspaceSecurityException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct);
        }
    }
}

public sealed class DetectDevelopmentRepositoryProfileEndpoint : Endpoint<DevelopmentProfileDetectionRequest, DevelopmentProfileDetectionResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service;

    public DetectDevelopmentRepositoryProfileEndpoint(IDevelopmentManagementService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

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
            var detection = await _service.DetectRepositoryProfileAsync(req.SelectedFolderId, ct);
            await Send.OkAsync(new DevelopmentProfileDetectionResponse { ProfileId = detection.ProfileId, BuildTarget = detection.BuildTarget, Candidates = detection.Candidates }, ct);
        }
        catch (Exception exception) when (exception is DevelopmentWorkspaceSecurityException or DirectoryNotFoundException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct);
        }
    }
}

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
        // Reconnect is the one Development endpoint whose request BOTH carries a folder to validate and acts on the
        // project's persisted binding, so it is the only one that has to split the workspace-security family by type:
        // the persisted binding blocking the reconnect is a 409, while the folder the caller just picked being
        // unusable (not a Git root, read-only, network path) is the same 400 it is on register/create.
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
