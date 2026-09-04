namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>One definition in full, graph included — what the editor opens.</summary>
public sealed class GetGraphWorkflowDefinitionEndpoint(IGraphWorkflowStore store) : Endpoint<GraphWorkflowDefinitionRequest, GraphWorkflowDefinitionResponse>
{
    private readonly IGraphWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Get(LocalApiRoutes.GraphWorkflows.DefinitionById);
        Policies(NodeAuthorizationPolicies.Operator);
        Description(static builder => builder.Produces(StatusCodes.Status404NotFound));
    }

    public override async Task HandleAsync(GraphWorkflowDefinitionRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);

        var definition = await _store.GetDefinitionAsync(req.DefinitionId, ct).ConfigureAwait(false);
        await Send.OkAsync(definition.ToResponse(), ct).ConfigureAwait(false);
    }
}
