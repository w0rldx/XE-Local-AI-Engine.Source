namespace XE_Local_AI_Engine.Client.Endpoints.Proxy.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Proxy;

/// <summary>
///     Revokes the inbound model-proxy credential. With no key stored the proxy authenticates nobody, which is the
///     documented way to turn the proxy off without changing configuration or restarting the node.
/// </summary>
public sealed class RevokeLocalModelProxyApiKeyEndpoint : EndpointWithoutRequest
{
    private readonly ILocalModelProxyApiKeyService _apiKeyService;

    public RevokeLocalModelProxyApiKeyEndpoint(ILocalModelProxyApiKeyService apiKeyService)
    {
        ArgumentNullException.ThrowIfNull(apiKeyService);
        _apiKeyService = apiKeyService;
    }

    public override void Configure()
    {
        Delete(LocalApiRoutes.Proxy.ApiKey);
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
