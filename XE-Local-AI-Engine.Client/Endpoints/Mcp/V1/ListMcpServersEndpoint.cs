namespace XE_Local_AI_Engine.Client.Endpoints.Mcp.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Mcp.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Mcp;

public sealed class ListMcpServersEndpoint(IMcpServerService mcpServerService)
    : EndpointWithoutRequest<ListMcpServersResponse>
{
    private readonly IMcpServerService _mcpServerService = mcpServerService ?? throw new ArgumentNullException(nameof(mcpServerService));

    public override void Configure()
    {
        Get(LocalApiRoutes.Mcp.Servers);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var records = await _mcpServerService.ListAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(new ListMcpServersResponse
            {
                Items = [.. records.Select(static record => record.ToResponse())]
            },
            ct).ConfigureAwait(false);
    }
}
