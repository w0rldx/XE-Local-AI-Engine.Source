namespace XE_Local_AI_Engine.Client.Endpoints.Mcp.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Mcp;

/// <summary>
///     Revokes the inbound-MCP credential. With no key stored the MCP endpoint authenticates nobody, which is the
///     documented way to turn the inbound surface off without changing configuration or restarting the node.
/// </summary>
public sealed class RevokeMcpServerApiKeyEndpoint : EndpointWithoutRequest
{
    private readonly IMcpServerApiKeyService _apiKeyService;

    public RevokeMcpServerApiKeyEndpoint(IMcpServerApiKeyService apiKeyService)
    {
        ArgumentNullException.ThrowIfNull(apiKeyService);
        _apiKeyService = apiKeyService;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Mcp.ServerApiKey);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var revoked = await _apiKeyService.RevokeAsync(ct);
        if (!revoked)
        {
            await Send.NotFoundAsync(ct);
            return;
        }

        await Send.NoContentAsync(ct);
    }
}
