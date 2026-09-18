namespace XE_Local_AI_Engine.Client.Endpoints.Mcp.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Mcp;

public sealed class DeleteMcpServerEndpoint : Endpoint<DeleteMcpServerRequest>
{
    private readonly IMcpServerService _mcpServerService;

    public DeleteMcpServerEndpoint(IMcpServerService mcpServerService)
    {
        ArgumentNullException.ThrowIfNull(mcpServerService);
        _mcpServerService = mcpServerService;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Mcp.ServerById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(DeleteMcpServerRequest req, CancellationToken ct)
    {
        var deleted = await _mcpServerService.DeleteAsync(req.McpServerId, ct);
        if (!deleted)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
