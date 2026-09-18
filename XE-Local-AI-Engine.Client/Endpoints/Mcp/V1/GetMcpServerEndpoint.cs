namespace XE_Local_AI_Engine.Client.Endpoints.Mcp.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Mcp.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Mcp;

public sealed class GetMcpServerEndpoint : Endpoint<GetMcpServerRequest, McpServerResponse>
{
    private readonly IMcpServerService _mcpServerService;

    public GetMcpServerEndpoint(IMcpServerService mcpServerService)
    {
        ArgumentNullException.ThrowIfNull(mcpServerService);
        _mcpServerService = mcpServerService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Mcp.ServerById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(GetMcpServerRequest req, CancellationToken ct)
    {
        var record = await _mcpServerService.GetByIdAsync(req.McpServerId, ct);
        if (record is null)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.OkAsync(record.ToResponse(), ct);
    }
}
