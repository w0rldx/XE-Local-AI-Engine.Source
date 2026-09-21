namespace XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.DevelopmentWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.DevWorkflows;

/// <summary>
///     Starts a run of one definition for one work item. 202, not 200: the endpoint commits a durable intent and the
///     dispatcher advances it out of band, so the body legitimately reads <c>Pending</c>.
/// </summary>
/// <remarks>
///     The two refusals both come from the runtime, which holds the pinned graph: a graph with repo-bound nodes on a
///     work item that names no project is a 400, and a work item that already has a live run is a 409.
/// </remarks>
public sealed class StartDevWorkflowRunEndpoint : Endpoint<StartDevWorkflowRunRequest, DevWorkflowRunResponse>
{
    private readonly DevWorkflowRunComposer _composer;
    private readonly IDevWorkflowRunService _runs;

    public StartDevWorkflowRunEndpoint(IDevWorkflowRunService runs, DevWorkflowRunComposer composer)
    {
        ArgumentNullException.ThrowIfNull(composer);
        ArgumentNullException.ThrowIfNull(runs);
        _composer = composer;
        _runs = runs;
    }

    public override void Configure()
    {
        Post(LocalApiRoutes.DevelopmentWorkflows.WorkItemRuns);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(builder => builder.Produces<DevWorkflowRunResponse>(StatusCodes.Status202Accepted)
                                      .ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                      .Produces(StatusCodes.Status404NotFound)
                                      .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(StartDevWorkflowRunRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var detail = await _runs.StartAsync(req.WorkItemId, req.DefinitionId, req.InputsJson, req.OperationId, ct);
        await Send.ResultAsync(Results.Accepted(value: await _composer.ComposeAsync(detail, ct)));
    }
}
