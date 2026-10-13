namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     A hard delete of a finished run with its node runs and events, refused with a 409 while the run is still live —
///     checked inside the store's transaction. Its definition is untouched.
/// </summary>
public sealed class DeleteGraphWorkflowRunEndpoint : Endpoint<GraphWorkflowRunRequest>
{
    private readonly IGraphWorkflowRunService _runs;

    public DeleteGraphWorkflowRunEndpoint(IGraphWorkflowRunService runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        _runs = runs;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.GraphWorkflows.RunById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.Produces(StatusCodes.Status204NoContent)
                                             .Produces(StatusCodes.Status404NotFound)
                                             .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(GraphWorkflowRunRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        await _runs.DeleteAsync(req.RunId, ct);
        await Send.NoContentAsync(ct);
    }
}
