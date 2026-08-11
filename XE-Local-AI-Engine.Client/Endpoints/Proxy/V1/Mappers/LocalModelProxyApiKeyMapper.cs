namespace XE_Local_AI_Engine.Client.Endpoints.Proxy.V1.Mappers;

using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Proxy;

/// <summary>
///     Maps the inbound model-proxy credential onto its transport shape and derives the OpenAI <c>base_url</c> an
///     external tool should be pointed at.
/// </summary>
internal static class LocalModelProxyApiKeyMapper
{
    public static LocalModelProxyApiKeyStatusResponse ToStatus(LocalModelProxyApiKeyView? view, HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return new LocalModelProxyApiKeyStatusResponse
        {
            Configured = view is not null,
            ApiKey = view is null ? null : ToApiKey(view),
            EndpointUrl = BuildEndpointUrl(httpContext)
        };
    }

    /// <summary>
    ///     Maps a freshly minted credential, carrying the one-time plaintext key. This is the only mapping that emits
    ///     the secret; <see cref="ToStatus" /> structurally cannot, because its response type has no key field.
    /// </summary>
    public static GeneratedLocalModelProxyApiKeyResponse ToGenerated(GeneratedLocalModelProxyApiKey generated, HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(generated);
        ArgumentNullException.ThrowIfNull(httpContext);

        return new GeneratedLocalModelProxyApiKeyResponse
        {
            Configured = true,
            ApiKey = ToApiKey(generated.View),
            EndpointUrl = BuildEndpointUrl(httpContext),
            Key = generated.Key
        };
    }

    private static LocalModelProxyApiKeyResponse ToApiKey(LocalModelProxyApiKeyView view)
    {
        return new LocalModelProxyApiKeyResponse
        {
            Prefix = view.Prefix,
            CreatedAt = view.CreatedAt,
            LastUsedAt = view.LastUsedAt
        };
    }

    /// <summary>
    ///     Builds the absolute OpenAI base URL from the LIVE request rather than from configuration. The node binds an
    ///     OS-assigned loopback port in desktop mode, so the port is not knowable ahead of time and any configured value
    ///     would go stale on the next launch; the request the operator's own browser just made carries the authoritative
    ///     scheme, host and port. An external tool sets this as its <c>base_url</c> and the client appends
    ///     <c>/chat/completions</c> etc.
    /// </summary>
    private static string BuildEndpointUrl(HttpContext httpContext)
    {
        var request = httpContext.Request;
        return $"{request.Scheme}://{request.Host.Value}/{LocalApiRoutes.Prefix}/{LocalApiRoutes.Proxy.OpenAiBase}";
    }
}
