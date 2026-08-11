namespace XE_Local_AI_Engine.Client.Endpoints.Proxy.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Proxy;

/// <summary>
///     Revokes the inbound model-proxy credential. With no key stored the proxy authenticates nobody, which is the
///     documented way to turn the proxy off without changing configuration or restarting the node.
/// </summary>
public sealed class RevokeLocalModelProxyApiKeyEndpoint(ILocalModelProxyApiKeyService apiKeyService)
    : EndpointWithoutRequest
{
    private readonly ILocalModelProxyApiKeyService _apiKeyService = apiKeyService ?? throw new ArgumentNullException(nameof(apiKeyService));

    public override void Configure()
    {
        Delete(LocalApiRoutes.Proxy.ApiKey);
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
