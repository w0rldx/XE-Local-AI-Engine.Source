namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>The definition picker's feed. Never loads a graph blob: the node count is a column, not a parse.</summary>
public sealed class ListGraphWorkflowDefinitionsEndpoint : EndpointWithoutRequest<ListGraphWorkflowDefinitionsResponse>
{
    private readonly IGraphWorkflowDefinitionService _definitions;

    public ListGraphWorkflowDefinitionsEndpoint(IGraphWorkflowDefinitionService definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _definitions = definitions;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.GraphWorkflows.Definitions);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var summaries = await _definitions.ListAsync(ct);
        await Send.OkAsync(new ListGraphWorkflowDefinitionsResponse
        {
            Definitions = [.. summaries.Select(GraphWorkflowContractMapper.ToResponse)]
        }, ct);
    }
}
