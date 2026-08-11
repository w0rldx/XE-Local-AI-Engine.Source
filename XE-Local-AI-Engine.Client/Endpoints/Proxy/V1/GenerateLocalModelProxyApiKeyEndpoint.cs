namespace XE_Local_AI_Engine.Client.Endpoints.Proxy.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Proxy.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Proxy;

/// <summary>
///     Mints a new inbound model-proxy credential, REPLACING any existing one. This is both "generate" and "rotate":
///     there is one key, so a regenerate immediately invalidates the previous value and every tool configured with it.
///     Generating a key is also how an operator turns the proxy ON — a node with no key authenticates nobody.
///     <para>
///         This response is the ONLY place the plaintext key ever appears — the node persists only its SHA-256 digest.
///         A caller that discards this body cannot get the key back from any other endpoint.
///     </para>
/// </summary>
public sealed class GenerateLocalModelProxyApiKeyEndpoint(ILocalModelProxyApiKeyService apiKeyService)
    : EndpointWithoutRequest<GeneratedLocalModelProxyApiKeyResponse>
{
    private readonly ILocalModelProxyApiKeyService _apiKeyService = apiKeyService ?? throw new ArgumentNullException(nameof(apiKeyService));

    public override void Configure()
    {
        Post(LocalApiRoutes.Proxy.ApiKey);
        Policies(NodeAuthorizationPolicies.Operator);
        // Route-only POST with no body and therefore no Content-Type; FastEndpoints' default Accepts metadata would
        // answer that with 415. Same override the scheduler's body-less actions use.
        Description(x => x.Accepts<EmptyRequest>());
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var generated = await _apiKeyService.GenerateAsync(ct).ConfigureAwait(false);
        await Send.OkAsync(LocalModelProxyApiKeyMapper.ToGenerated(generated, HttpContext), ct).ConfigureAwait(false);
    }
}
