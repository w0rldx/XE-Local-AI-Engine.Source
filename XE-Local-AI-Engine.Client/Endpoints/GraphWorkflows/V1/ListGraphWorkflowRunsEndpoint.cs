namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Entities;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>The run list, newest first. No node runs and no documents: this is the page that picks a run to open.</summary>
public sealed class ListGraphWorkflowRunsEndpoint(IGraphWorkflowRunService runs) : Endpoint<ListGraphWorkflowRunsRequest, ListGraphWorkflowRunsResponse>
{
    private readonly IGraphWorkflowRunService _runs = runs ?? throw new ArgumentNullException(nameof(runs));

    public override void Configure()
    {
        Get(LocalApiRoutes.GraphWorkflows.Runs);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest));
    }

    public override async Task HandleAsync(ListGraphWorkflowRunsRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        // Safe to parse rather than TryParse: the validator has already refused anything that is not a member name.
        var status = req.Status is null ? (GraphWorkflowRunStatus?)null : Enum.Parse<GraphWorkflowRunStatus>(req.Status, ignoreCase: true);
        var listed = await _runs.ListRunsAsync(status, req.Limit, ct).ConfigureAwait(false);
        await Send.OkAsync(new ListGraphWorkflowRunsResponse([.. listed.Select(GraphWorkflowContractMapper.ToResponse)]), ct).ConfigureAwait(false);
    }
}
