namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Persistence.Stores;
using XE_Local_AI_Engine.Client.Services.Auth;

/// <summary>The definition picker's feed. Never loads a graph blob: the node count is a column, not a parse.</summary>
public sealed class ListGraphWorkflowDefinitionsEndpoint(IGraphWorkflowStore store) : EndpointWithoutRequest<ListGraphWorkflowDefinitionsResponse>
{
    private readonly IGraphWorkflowStore _store = store ?? throw new ArgumentNullException(nameof(store));

    public override void Configure()
    {
        Get(LocalApiRoutes.GraphWorkflows.Definitions);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var definitions = await _store.ListDefinitionsAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ListGraphWorkflowDefinitionsResponse([.. definitions.Select(GraphWorkflowContractMapper.ToResponse)]), ct).ConfigureAwait(false);
    }
}
