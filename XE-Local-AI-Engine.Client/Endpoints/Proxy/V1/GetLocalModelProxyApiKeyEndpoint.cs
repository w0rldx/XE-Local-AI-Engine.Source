namespace XE_Local_AI_Engine.Client.Endpoints.Proxy.V1;

using FastEndpoints;
using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Endpoints.Proxy.V1.Mappers;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Proxy;

/// <summary>
///     The inbound model-proxy credential's non-secret metadata (prefix, timestamps) plus the OpenAI base URL to point
///     a tool at.
/// </summary>
/// <remarks>
///     It cannot return the key itself: the node keeps only a one-way digest, and the response type has no field for
///     one. Answers 200 reporting "not configured" rather than 404 when no key exists, so the settings page can render
///     the empty state from one call.
/// </remarks>
public sealed class GetLocalModelProxyApiKeyEndpoint : EndpointWithoutRequest<LocalModelProxyApiKeyStatusResponse>
{
    private readonly ILocalModelProxyApiKeyService _apiKeyService;

    public GetLocalModelProxyApiKeyEndpoint(ILocalModelProxyApiKeyService apiKeyService)
    {
        ArgumentNullException.ThrowIfNull(apiKeyService);
        _apiKeyService = apiKeyService;
    }

    public override void Configure()
    {
        Get(LocalApiRoutes.Proxy.ApiKey);
        Policies(NodeAuthorizationPolicies.Operator);
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        var view = await _apiKeyService.GetAsync(ct);
        await Send.OkAsync(LocalModelProxyApiKeyMapper.ToStatus(view, HttpContext), ct);
    }
}
