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
            ApiKey = view is null
                ? null
                : new McpServerApiKeyResponse
                {
                    Prefix = view.Prefix,
                    Key = view.Key,
                    CreatedAt = view.CreatedAt,
                    LastUsedAt = view.LastUsedAt
                },
            EndpointUrl = BuildEndpointUrl(httpContext)
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
