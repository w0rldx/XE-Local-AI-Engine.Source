namespace XE_Local_AI_Engine.Client.Endpoints.Mcp.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Mcp;

/// <summary>
///     Revokes the inbound-MCP credential. With no key stored the MCP endpoint authenticates nobody, which is the
///     documented way to turn the inbound surface off without changing configuration or restarting the node.
/// </summary>
public sealed class RevokeMcpServerApiKeyEndpoint(IMcpServerApiKeyService apiKeyService)
    : EndpointWithoutRequest
{
    private readonly IMcpServerApiKeyService _apiKeyService = apiKeyService ?? throw new ArgumentNullException(nameof(apiKeyService));

    public override void Configure()
    {
        Delete(LocalApiRoutes.Mcp.ServerApiKey);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var revoked = await _apiKeyService.RevokeAsync(ct).ConfigureAwait(false);
        if (!revoked)
        {
            await Send.NotFoundAsync(ct).ConfigureAwait(false);
            return;
        }

        await Send.NoContentAsync(ct).ConfigureAwait(false);
    }
}
