namespace XE_Local_AI_Engine.Client.Endpoints.Mcp.V1.Mappers;

using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Mcp;

/// <summary>
///     Maps the inbound-MCP credential onto its transport shape and derives the endpoint URL an external client should
///     be pointed at.
/// </summary>
internal static class McpServerApiKeyMapper
{
    public static McpServerApiKeyStatusResponse ToStatus(McpServerApiKeyView? view, HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        return new McpServerApiKeyStatusResponse
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
    public static GeneratedMcpServerApiKeyResponse ToGenerated(GeneratedMcpServerApiKey generated, HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(generated);
        ArgumentNullException.ThrowIfNull(httpContext);

        return new GeneratedMcpServerApiKeyResponse
        {
            Configured = true,
            ApiKey = ToApiKey(generated.View),
            EndpointUrl = BuildEndpointUrl(httpContext),
            Key = generated.Key
        };
    }

    private static McpServerApiKeyResponse ToApiKey(McpServerApiKeyView view)
    {
        return new McpServerApiKeyResponse
        {
            Prefix = view.Prefix,
            CreatedAt = view.CreatedAt,
            LastUsedAt = view.LastUsedAt
        };
    }

    /// <summary>
    ///     Builds the absolute MCP endpoint URL from the LIVE request rather than from configuration. The node binds an
    ///     OS-assigned loopback port in desktop mode, so the port is not knowable ahead of time and any configured value
    ///     would go stale on the next launch; the request the operator's own browser just made carries the authoritative
    ///     scheme, host and port.
    /// </summary>
    private static string BuildEndpointUrl(HttpContext httpContext)
    {
        var request = httpContext.Request;
        return $"{request.Scheme}://{request.Host.Value}/{LocalApiRoutes.Prefix}/{LocalApiRoutes.Mcp.ServerEndpoint}";
    }
}
