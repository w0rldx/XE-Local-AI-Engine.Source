namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     Asks the run to cancel. 202, and the body reads <c>Cancelling</c>: live node runs drain first — each lane is
///     asked to stop rather than having a terminal status written over work still in flight — so a 200 would tell a
///     schema-trusting client the cancel had finished. A run that is already terminal answers 409.
/// </summary>
public sealed class CancelGraphWorkflowRunEndpoint(IGraphWorkflowRunService runs) : Endpoint<GraphWorkflowRunRequest, GraphWorkflowRunResponse>
{
    private readonly IGraphWorkflowRunService _runs = runs ?? throw new ArgumentNullException(nameof(runs));

    public override void Configure()
    {
        Post(LocalApiRoutes.GraphWorkflows.RunCancel);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.Produces<GraphWorkflowRunResponse>(StatusCodes.Status202Accepted)
                                             .Produces(StatusCodes.Status404NotFound)
                                             .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(GraphWorkflowRunRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var detail = await _runs.CancelAsync(req.RunId, ct).ConfigureAwait(false);
        await Send.ResultAsync(Results.Accepted(value: detail.ToResponse())).ConfigureAwait(false);
    }
}
