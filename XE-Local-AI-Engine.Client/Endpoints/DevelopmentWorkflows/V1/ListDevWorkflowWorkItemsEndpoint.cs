namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     The work-item list. Each row carries its latest run's status and node counters, so the page renders without a
///     per-row fetch — which is what makes polling it honest rather than a fan-out.
/// </summary>
public sealed class ListDevWorkflowWorkItemsEndpoint : Endpoint<ListDevWorkflowWorkItemsRequest, ListDevWorkflowWorkItemsResponse>
{
    private readonly DevWorkflowAuthoringService _authoring;

    public ListDevWorkflowWorkItemsEndpoint(DevWorkflowAuthoringService authoring)
    {
        ArgumentNullException.ThrowIfNull(authoring);
        _authoring = authoring;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.DevelopmentWorkflows.WorkItems);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(ListDevWorkflowWorkItemsRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // Safe to parse rather than TryParse: the validator has already refused anything that is not a member.
        var status = req.Status is null ? (DevWorkflowWorkItemStatus?)null : Enum.Parse<DevWorkflowWorkItemStatus>(req.Status, ignoreCase: true);
        var items = await _authoring.ListWorkItemsAsync(status, ct);
        await Send.OkAsync(new ListDevWorkflowWorkItemsResponse { Items = [.. items.Select(DevWorkflowContractMapper.ToSummaryResponse)] }, ct);
    }
}
