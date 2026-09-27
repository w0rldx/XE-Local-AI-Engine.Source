namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.GraphWorkflows;

/// <summary>
///     The Tool node picker's feed. Reads the same list the save gate checks — the invocation envelope plus the
///     allow-listed <c>web_fetch</c> exception — so a name offered here is a name a run will accept, and the filter is
///     never re-stated at this layer.
/// </summary>
public sealed class ListGraphWorkflowToolsEndpoint : EndpointWithoutRequest<ListGraphWorkflowToolsResponse>
{
    private readonly IGraphWorkflowDefinitionService _definitions;

    public ListGraphWorkflowToolsEndpoint(IGraphWorkflowDefinitionService definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        _definitions = definitions;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.GraphWorkflows.Tools);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var invocable = await _definitions.ListToolsAsync(ct);
        await Send.OkAsync(new ListGraphWorkflowToolsResponse
        {
            Tools = [.. invocable.Select(GraphWorkflowToolMapper.ToResponse)]
        }, ct);
    }
}
