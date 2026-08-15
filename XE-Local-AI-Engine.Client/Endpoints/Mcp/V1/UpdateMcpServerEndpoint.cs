namespace XE_Local_AI_Engine.Client.Endpoints.Mcp.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Mcp.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Mcp;

public sealed class UpdateMcpServerEndpoint(IMcpServerService mcpServerService)
    : Endpoint<UpdateMcpServerRequest, McpServerResponse>
{
    private readonly IMcpServerService _mcpServerService = mcpServerService ?? throw new ArgumentNullException(nameof(mcpServerService));

    public override void Configure()
    {
        Put(LocalApiRoutes.Mcp.ServerById);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(UpdateMcpServerRequest req, CancellationToken ct)
    {
        var record = await _mcpServerService.UpdateAsync(req.McpServerId, req.ToInput(), ct).ConfigureAwait(false);
        if (record is null)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.OkAsync(record.ToResponse(), ct).ConfigureAwait(false);
    }
}
