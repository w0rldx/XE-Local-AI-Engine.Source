namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     One run with every node run's summary AND the graph the run pinned at start — what the run view draws, without
///     having to read the definition and hope it has not been edited since.
/// </summary>
/// <remarks>
///     Deliberately WITHOUT the node-run documents: they are the largest thing a run stores, and a graph of two
///     hundred nodes would carry all of them on a page that renders none.
/// </remarks>
public sealed class GetGraphWorkflowRunEndpoint : Endpoint<GraphWorkflowRunRequest, GraphWorkflowRunResponse>
{
    private readonly IGraphWorkflowRunService _runs;

    public GetGraphWorkflowRunEndpoint(IGraphWorkflowRunService runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        _runs = runs;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.GraphWorkflows.RunById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(GraphWorkflowRunRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var detail = await _runs.GetRunAsync(req.RunId, ct);
        await Send.OkAsync(detail.ToResponse(), ct);
    }
}
