namespace XE_Local_AI_Engine.Client.Hosting.Pipeline;

using XE_Local_AI_Engine.Client.Endpoints.Common;
using XE_Local_AI_Engine.Client.Services.Auth;
using XE_Local_AI_Engine.Client.Services.Integrations;
using XE_Local_AI_Engine.Client.Services.Proxy;

/// <summary>
///     Maps the hand-mapped, API-key-authenticated inbound routes: the MCP server endpoint, the OpenAI-compatible
///     model proxy and the external integration API, each behind its own policy and rate limit.
/// </summary>
internal static class InboundApiEndpointExtensions
{
    internal static WebApplication MapNodeInboundApis(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // The inbound MCP Streamable HTTP endpoint, mapped beside the hubs. Its path MUST stay inside /api/local/v1 — do not move it — and the McpServer policy accepts
        // ONLY the MCP API key scheme, never the operator's JWT. See docs/wiki/09-api-and-hubs.md ("Notable non-typed routes").
        app.MapMcp($"/{LocalApiRoutes.Prefix}/{LocalApiRoutes.Mcp.ServerEndpoint}")
           .RequireAuthorization(NodeAuthorizationPolicies.McpServer)
           .RequireRateLimiting(NodeAuthRateLimits.McpPolicy);

        // The inbound OpenAI-compatible model proxy, hand-mapped like MapMcp and for the same reasons; its paths MUST stay inside /api/local/v1. The LocalModelProxy
        // policy accepts ONLY the model-proxy API key scheme, never the operator's JWT or the MCP key. See docs/wiki/09-api-and-hubs.md ("Notable non-typed routes").
        var proxyRoutePrefix = $"/{LocalApiRoutes.Prefix}/";
        app.MapGet(proxyRoutePrefix + LocalApiRoutes.Proxy.Models,
               static (HttpContext context, LocalModelProxyForwarder forwarder) => forwarder.WriteModelsAsync(context))
           .RequireAuthorization(NodeAuthorizationPolicies.LocalModelProxy)
           .RequireRateLimiting(NodeAuthRateLimits.LocalModelProxyPolicy);
        app.MapPost(proxyRoutePrefix + LocalApiRoutes.Proxy.ChatCompletions,
               static (HttpContext context, LocalModelProxyForwarder forwarder) => forwarder.ForwardChatCompletionsAsync(context))
           .RequireAuthorization(NodeAuthorizationPolicies.LocalModelProxy)
           .RequireRateLimiting(NodeAuthRateLimits.LocalModelProxyPolicy);
        app.MapPost(proxyRoutePrefix + LocalApiRoutes.Proxy.Embeddings,
               static (HttpContext context, LocalModelProxyForwarder forwarder) => forwarder.ForwardEmbeddingsAsync(context))
           .RequireAuthorization(NodeAuthorizationPolicies.LocalModelProxy)
           .RequireRateLimiting(NodeAuthRateLimits.LocalModelProxyPolicy);

        // The inbound EXTERNAL integration API, hand-mapped like MapMcp and the model proxy and for the same reasons; its paths MUST stay inside /api/local/v1. The
        // IntegrationApi policy accepts ONLY the integration key scheme. See docs/wiki/09-api-and-hubs.md ("Notable non-typed routes").
        var integrationRoutePrefix = $"/{LocalApiRoutes.Prefix}/";

        // Composition-time value with ONE authority: endpoint metadata is baked at map time, so the cap must not come from an IOptions<IntegrationOptions> resolved per
        // request, which would be a second source that can disagree with what the route already carries. From configuration, the same shape the permit constants use.
        var integrationMaxRequestBodyBytes = app.Configuration.GetValue($"{IntegrationOptions.Section}:{nameof(IntegrationOptions.MaxRequestBodyBytes)}",
            defaultValue: 1024L * 1024L);

        app.MapPost(integrationRoutePrefix + LocalApiRoutes.IntegrationApi.Invoke,
               static (HttpContext context, IntegrationApiHandler handler) => handler.InvokeAsync(context))
           .RequireAuthorization(NodeAuthorizationPolicies.IntegrationApi)
           .RequireRateLimiting(NodeAuthRateLimits.IntegrationApiPolicy)
           .WithMetadata(new IntegrationRequestSizeLimit(integrationMaxRequestBodyBytes));
        app.MapGet(integrationRoutePrefix + LocalApiRoutes.IntegrationApi.ExecutionById,
               static (HttpContext context, IntegrationApiHandler handler) => handler.GetExecutionAsync(context))
           .RequireAuthorization(NodeAuthorizationPolicies.IntegrationApi)
           .RequireRateLimiting(NodeAuthRateLimits.IntegrationApiPolicy);
        app.MapGet(integrationRoutePrefix + LocalApiRoutes.IntegrationApi.ExecutionEvents,
               static (HttpContext context, IntegrationApiHandler handler) => handler.GetExecutionEventsAsync(context))
           .RequireAuthorization(NodeAuthorizationPolicies.IntegrationApi)
           .RequireRateLimiting(NodeAuthRateLimits.IntegrationApiPolicy);
        app.MapPost(integrationRoutePrefix + LocalApiRoutes.IntegrationApi.ExecutionCancel,
               static (HttpContext context, IntegrationApiHandler handler) => handler.CancelExecutionAsync(context))
           .RequireAuthorization(NodeAuthorizationPolicies.IntegrationApi)
           .RequireRateLimiting(NodeAuthRateLimits.IntegrationApiPolicy);
        app.MapGet(integrationRoutePrefix + LocalApiRoutes.IntegrationApi.SessionById,
               static (HttpContext context, IntegrationApiHandler handler) => handler.GetSessionAsync(context))
           .RequireAuthorization(NodeAuthorizationPolicies.IntegrationApi)
           .RequireRateLimiting(NodeAuthRateLimits.IntegrationApiPolicy);

        return app;
    }
}
