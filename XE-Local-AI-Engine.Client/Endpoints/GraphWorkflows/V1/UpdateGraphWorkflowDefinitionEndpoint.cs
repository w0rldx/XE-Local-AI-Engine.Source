namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     Edits a definition under the version it was edited from. A stale version answers 409 through the global
///     conflict handler rather than overwriting whatever landed in between.
/// </summary>
public sealed class UpdateGraphWorkflowDefinitionEndpoint(IGraphWorkflowDefinitionService definitions)
    : Endpoint<UpdateGraphWorkflowDefinitionRequest, GraphWorkflowDefinitionResponse>
{
    private readonly IGraphWorkflowDefinitionService _definitions = definitions ?? throw new ArgumentNullException(nameof(definitions));

    public override void Configure()
    {
        Put(LocalApiRoutes.GraphWorkflows.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
        Options(static builder => builder.WithMetadata(new GraphWorkflowRequestSizeLimit()));
        Description(static builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                             .Produces(StatusCodes.Status404NotFound)
                                             .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
                                             .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(UpdateGraphWorkflowDefinitionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        if (GraphWorkflowRequestSizeLimit.RefuseIfOversized(HttpContext.Request, this))
        {
            await Send.ErrorsAsync(StatusCodes.Status413PayloadTooLarge, ct).ConfigureAwait(false);
            return;
        }

        // A null graph leaves the stored one alone — a rename must not have to echo a graph back to keep it. Runs that
        // already pinned this definition are unaffected either way: they carry their own snapshot.
        var graphJson = req.Graph is { } graph ? GraphWorkflowContractMapper.ToGraphJson(graph) : null;

        try
        {
            var updated = await _definitions.UpdateAsync(req.DefinitionId, req.Version, req.Name, req.Description, graphJson, ct).ConfigureAwait(false);
            await Send.OkAsync(updated.ToResponse(), ct).ConfigureAwait(false);
        }
        catch (GraphWorkflowValidationException exception)
        {
            GraphWorkflowValidationErrors.AddTo(this, exception.Result);
            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
