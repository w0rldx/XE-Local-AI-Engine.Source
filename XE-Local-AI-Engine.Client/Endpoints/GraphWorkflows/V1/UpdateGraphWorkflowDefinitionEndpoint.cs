namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using FluentValidation.Results;
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
        Description(static builder => builder.ProducesProblemDetails(StatusCodes.Status400BadRequest)
                                             .Produces(StatusCodes.Status404NotFound)
                                             .ProducesConflictProblemDetails());
    }

    public override async Task HandleAsync(UpdateGraphWorkflowDefinitionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

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
            foreach (var error in exception.Result.Errors)
            {
                // The node or edge key becomes the failure's property name, which is what lets the editor draw the
                // complaint on the offending element. A whole-document failure has no element, so it goes to
                // FastEndpoints' own general-errors field rather than to a key nothing on the canvas answers to.
                if (error.Key is { } key)
                {
                    AddError(new ValidationFailure(key, error.Message));
                }
                else
                {
                    AddError(error.Message);
                }
            }

            await Send.ErrorsAsync(cancellation: ct).ConfigureAwait(false);
        }
    }
}
