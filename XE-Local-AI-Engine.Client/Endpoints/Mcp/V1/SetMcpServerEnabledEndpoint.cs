namespace XE_Local_AI_Engine.Client.Endpoints.Mcp.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Mcp.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Mcp;

/// <summary>
///     Enables or disables a registered MCP server. Enabling is a deliberate, separate action from create/update (a
///     registration is always created disabled), and is the only path that flips the enabled state — the
///     create/update bodies carry no enabled flag. A successful toggle triggers a connection refresh in the service.
/// </summary>
public sealed class SetMcpServerEnabledEndpoint(IMcpServerService mcpServerService)
    : Endpoint<SetMcpServerEnabledRequest, McpServerResponse>
{
    private readonly IMcpServerService _mcpServerService = mcpServerService ?? throw new ArgumentNullException(nameof(mcpServerService));

    public override void Configure()
    {
        Patch(LocalApiRoutes.Mcp.ServerEnabled);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(SetMcpServerEnabledRequest req, CancellationToken ct)
    {
        var record = await _mcpServerService.SetEnabledAsync(req.McpServerId, req.Enabled, ct).ConfigureAwait(false);
        if (record is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(record.ToResponse(), ct).ConfigureAwait(false);
    }
}
