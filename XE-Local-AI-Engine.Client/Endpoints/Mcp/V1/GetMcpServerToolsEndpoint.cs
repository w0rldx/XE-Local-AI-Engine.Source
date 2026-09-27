namespace XE_Local_AI_Engine.Client.Endpoints.Mcp.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Mcp.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Mcp;

/// <summary>
///     The live connection state plus discovered tools for one registered MCP server.
/// </summary>
/// <remarks>
///     The status verdict (disabled / connecting / connected / error) is decided by
///     <see cref="IMcpServerService.GetToolsViewAsync" /> from the registration's enabled flag and the connection
///     manager's last refresh; see <see cref="McpServerToolsStatus" />.
/// </remarks>
public sealed class GetMcpServerToolsEndpoint : Endpoint<GetMcpServerToolsRequest, McpServerToolsResponse>
{
    private readonly IMcpServerService _mcpServerService;

    public GetMcpServerToolsEndpoint(IMcpServerService mcpServerService)
    {
        ArgumentNullException.ThrowIfNull(mcpServerService);
        _mcpServerService = mcpServerService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Mcp.ServerTools);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetMcpServerToolsRequest req, CancellationToken ct)
    {
        var view = await _mcpServerService.GetToolsViewAsync(req.McpServerId, ct);
        if (view is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(view.ToResponse(), ct);
    }
}
