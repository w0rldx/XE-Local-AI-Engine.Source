namespace XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.GraphWorkflows.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Tools;

/// <summary>
///     The Tool node picker's feed. Reads the same service the runtime invokes through, so a name offered here is a
///     name a run will accept — the filter is never re-stated at this layer.
/// </summary>
public sealed class ListGraphWorkflowToolsEndpoint : EndpointWithoutRequest<ListGraphWorkflowToolsResponse>
{
    private readonly IToolInvocationService _tools;

    public ListGraphWorkflowToolsEndpoint(IToolInvocationService tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _tools = tools;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.GraphWorkflows.Tools);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var invocable = await _tools.ListInvocableToolsAsync(ct);
        await Send.OkAsync(new ListGraphWorkflowToolsResponse([.. invocable.Select(GraphWorkflowToolMapper.ToResponse)]), ct);
    }
}
