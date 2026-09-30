namespace XE_Local_AI_Engine.Client.Endpoints.Common;

using System.Net;
using Microsoft.Net.Http.Headers;

/// <summary>
///     Answers 405/415 on the inbound MCP endpoint for verbs and bodies the stateless Streamable HTTP server never maps,
///     so a wrong verb or content type does not fall through to the JWT fallback policy and read as a rejected key.
/// </summary>
/// <remarks>
///     Runs after routing and before authentication: the SDK maps only POST in stateless mode, and an unmapped GET/DELETE
///     (or a POST whose content type fails the endpoint's Accepts metadata) otherwise reaches the fallback authorization
///     with no MCP endpoint selected. Pinned by <c>McpServerInboundAuthTests</c>.
/// </remarks>
public static class McpEndpointMethodGuard
{
    private const string AllowedMethod = "POST";

    public static IApplicationBuilder UseMcpEndpointMethodGuard(this IApplicationBuilder app, PathString mcpPath)
    {
        return app.Use(async (context, next) =>
        {
            if (!context.Request.Path.Equals(mcpPath, StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }

            if (!HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                context.Response.Headers[HeaderNames.Allow] = AllowedMethod;
                return;
            }

            if (!MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var mediaType)
                || !mediaType.MediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.UnsupportedMediaType;
                return;
            }

            await next(context);
        });
    }
}
