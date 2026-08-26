namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class ListDevelopmentProjectsEndpoint(IDevelopmentManagementService service)
    : EndpointWithoutRequest<ListDevelopmentProjectsResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.Development.Projects);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var projects = await _service.ListProjectsAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ListDevelopmentProjectsResponse(projects.Select(DevelopmentContractMapper.ToResponse).ToArray()), ct).ConfigureAwait(false);
    }
}

public sealed class CreateDevelopmentProjectEndpoint(IDevelopmentManagementService service)
    : Endpoint<CreateDevelopmentProjectRequest, DevelopmentProjectDetailResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Post(LocalApiRoutes.Development.Projects);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesProblemDetails(StatusCodes.Status409Conflict));
    }

    public override async Task HandleAsync(CreateDevelopmentProjectRequest req, CancellationToken ct)
    {
        if (!Enum.TryParse<DevelopmentEgressPolicy>(req.EgressPolicy, ignoreCase: true, out var egressPolicy))
        {
            AddError("The Development egress policy is invalid.");
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var result = await _service.CreateProjectAsync(new DevelopmentCreateProjectInput(req.OperationId,
                                               req.SelectedFolderId,
                                               req.Objective,
                                               req.BaseBranch,
                                               req.TaskTitle,
                                               req.Requirements,
                                               req.AcceptanceCriteriaJson,
                                               egressPolicy,
                                               req.CoderModelId,
                                               req.ReviewerModelId,
                                               req.TrustedRepositoryAcknowledged,
                                               req.MaxTokens,
                                               req.MaxDurationSeconds,
                                               req.CommandProfileId,
                                               req.BuildTarget),
                                           ct)
                                       .ConfigureAwait(false);
            await Send.OkAsync(result.ToResponse(), ct).ConfigureAwait(false);
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

public sealed class GetDevelopmentProjectEndpoint(IDevelopmentManagementService service)
    : Endpoint<DevelopmentProjectRequest, DevelopmentProjectDetailResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service = service ?? throw new ArgumentNullException(nameof(service));

    public override void Configure()
    {
        Get(LocalApiRoutes.Development.ProjectById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DevelopmentProjectRequest req, CancellationToken ct)
    {
        try
        {
            await Send.OkAsync((await _service.GetProjectAsync(req.ProjectId, ct).ConfigureAwait(false)).ToResponse(), ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
        }
    }
}
