namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     A hard delete, refused with a 409 while any run that pins this definition is still live — checked inside the
///     store's transaction. Terminal runs are unaffected: each pinned its own copy of the graph at start, so history
///     survives the row.
/// </summary>
public sealed class DeleteGraphWorkflowDefinitionEndpoint : Endpoint<GraphWorkflowDefinitionRequest>
{
    private readonly IGraphWorkflowDefinitionService _definitions;

    public DeleteGraphWorkflowDefinitionEndpoint(IGraphWorkflowDefinitionService definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _definitions = definitions;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.GraphWorkflows.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.Produces(StatusCodes.Status204NoContent)
                                             .Produces(StatusCodes.Status404NotFound)
                                             .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(GraphWorkflowDefinitionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        await _definitions.DeleteAsync(req.DefinitionId, ct);
        await Send.NoContentAsync(ct);
    }
}
