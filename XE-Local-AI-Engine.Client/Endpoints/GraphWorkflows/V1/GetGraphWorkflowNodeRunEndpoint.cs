namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     One node run in full, input and output documents included — the node drawer's read, and the one place a
///     document is decrypted. Keyed by node key rather than by row id: there is exactly one node run per
///     <c>(run, node key)</c>, so the key a reader already has off the canvas is its identity.
/// </summary>
public sealed class GetGraphWorkflowNodeRunEndpoint(IGraphWorkflowRunService runs) : Endpoint<GraphWorkflowNodeRunRequest, GraphWorkflowNodeRunResponse>
{
    private readonly IGraphWorkflowRunService _runs = runs ?? throw new ArgumentNullException(nameof(runs));

    public override void Configure()
    {
        Get(LocalApiRoutes.GraphWorkflows.RunNodeByKey);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest).Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(GraphWorkflowNodeRunRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var nodeRun = await _runs.GetNodeRunAsync(req.RunId, req.NodeKey, ct).ConfigureAwait(false);
        await Send.OkAsync(nodeRun.ToResponse(), ct).ConfigureAwait(false);
    }
}
