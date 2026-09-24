namespace XE_Local_AI_Engine.Client.Endpoints.Development.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Development.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Development;

public sealed class CreateDevelopmentProjectEndpoint : Endpoint<CreateDevelopmentProjectRequest, DevelopmentProjectDetailResponse>, IDevelopmentEndpoint
{
    private readonly IDevelopmentManagementService _service;

    public CreateDevelopmentProjectEndpoint(IDevelopmentManagementService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
    }

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
            await Send.ErrorsAsync(cancellation: ct);
            return;
        }

        try
        {
            var result = await _service.CreateProjectAsync(new DevelopmentCreateProjectInput
                {
                    OperationId = req.OperationId,
                    SelectedFolderId = req.SelectedFolderId,
                    Objective = req.Objective,
                    BaseBranch = req.BaseBranch,
                    TaskTitle = req.TaskTitle,
                    Requirements = req.Requirements,
                    AcceptanceCriteriaJson = req.AcceptanceCriteriaJson,
                    EgressPolicy = egressPolicy,
                    CoderModelId = req.CoderModelId,
                    ReviewerModelId = req.ReviewerModelId,
                    TrustedRepositoryAcknowledged = req.TrustedRepositoryAcknowledged,
                    MaxTokens = req.MaxTokens,
                    MaxDurationSeconds = req.MaxDurationSeconds,
                    CommandProfileId = req.CommandProfileId,
                    BuildTarget = req.BuildTarget
                },
                ct);
            await Send.OkAsync(result.ToResponse(), ct);
        }
        catch (Exception exception) when (exception is ArgumentException or DevelopmentWorkspaceSecurityException)
        {
            AddError(exception.Message);
            await Send.ErrorsAsync(cancellation: ct);
        }
    }
}
