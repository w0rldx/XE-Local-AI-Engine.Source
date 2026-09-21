namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     Creates a work item. Deliberately definition-agnostic: the definition is chosen per RUN, which is what lets one
///     work item be re-run against a revised definition later.
/// </summary>
public sealed class CreateDevWorkflowWorkItemEndpoint : Endpoint<CreateDevWorkflowWorkItemRequest, DevWorkflowWorkItemResponse>
{
    private readonly DevWorkflowAuthoringService _authoring;

    public CreateDevWorkflowWorkItemEndpoint(DevWorkflowAuthoringService authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        _authoring = authoring;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.DevelopmentWorkflows.WorkItems);
        Policies(NodeAuthorizationPolicies.Operator);
        // 201 is what the success path actually sends, so it is declared: the generated client narrows the create
        // response off this, and a route documented as 400-only would type no success body at all.
        Description(static builder => builder.Produces<DevWorkflowWorkItemResponse>(StatusCodes.Status201Created)
                                             .ProducesProblemDetails(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(CreateDevWorkflowWorkItemRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var created = await _authoring.CreateWorkItemAsync(new CreateDevWorkflowWorkItemCommand { WorkItemId = Guid.NewGuid(), Title = req.Title, Request = req.Request, DevelopmentProjectId = req.DevelopmentProjectId }, ct);
        await Send.CreatedAtAsync<GetDevWorkflowWorkItemEndpoint>(new
            {
                workItemId = created.Id
            },
            created.ToResponse([]),
            cancellation: ct);
    }
}
