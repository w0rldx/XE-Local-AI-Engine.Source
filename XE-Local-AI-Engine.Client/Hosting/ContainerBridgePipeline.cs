namespace XE_Local_AI_Engine.Client.Hosting;

using System.Net.Mime;
using Microsoft.Net.Http.Headers;
using XE_Local_AI_Engine.Client.Services.Containers.Bridge;
using XE_Local_AI_Engine.Client.Services.Proxy;

/// <summary>
///     Branches the bridge listener's connections into a pipeline of their own, first in the application pipeline.
///     <para>
///         The branch is what makes the bridge a separate surface rather than a second door onto the existing one.
///         Everything the node serves — the SPA bundle, <c>/api/local/v1</c>, the SignalR hubs, the MCP endpoint —
///         is registered AFTER this, so a request that arrived on the bridge port never reaches any of it; and the
///         branch predicate is the arrival port, so nothing mapped inside it is reachable on the main listener.
///     </para>
/// </summary>
internal static class ContainerBridgePipeline
{
    /// <summary>
    ///     The bridge answers in the OpenAI-style error envelope its callers understand, not RFC 7807: on this
    ///     listener there is no SPA fallback to reach and an unmapped path is genuinely nothing.
    /// </summary>
    internal const string NotFoundBody =
        """{"error":{"message":"No such container bridge route.","type":"invalid_request_error","code":null}}""";

    internal const string MethodNotAllowedBody =
        """{"error":{"message":"That container bridge route does not accept this HTTP method.","type":"invalid_request_error","code":null}}""";

    /// <summary>
    ///     The OpenAI-compatible surface a container calls, deliberately OUTSIDE <c>/api/local/v1</c>. Inside that
    ///     prefix <c>LocalApiSecurityMiddleware</c>'s loopback-peer check would reject exactly the traffic the
    ///     bridge exists to accept; the peer guard and the token gate above are what replace it.
    /// </summary>
    internal const string ChatCompletionsPath = "/llm/v1/chat/completions";

    internal const string EmbeddingsPath = "/llm/v1/embeddings";

    internal const string ModelsPath = "/llm/v1/models";

    /// <summary>The configuration key <c>HostFilteringMiddleware</c> reads its allow list from.</summary>
    private const string AllowedHostsKey = "AllowedHosts";

    /// <summary>
    ///     Adds the bridge's own host names to <c>AllowedHosts</c>, without which the listener is unreachable.
    ///     <para>
    ///         <c>HostFilteringMiddleware</c> is installed by an <c>IStartupFilter</c>, so it runs BEFORE every
    ///         middleware the composition root registers — the bridge branch included. The shipped
    ///         <c>AllowedHosts</c> is <c>localhost;127.0.0.1;[::1]</c>, which is exactly right for a loopback-only
    ///         node and exactly wrong for the one listener that is deliberately not loopback: a container's
    ///         <c>Host: 172.20.0.1:18790</c> is refused with 400 before anything can look at it.
    ///     </para>
    ///     <para>
    ///         Widening the list widens it for the loopback listener too, since host filtering is per host and not
    ///         per endpoint. That costs nothing the node was relying on: <c>LocalApiSecurityMiddleware</c> checks
    ///         Host and Origin against its own list for every <c>/api/local/v1</c> request and rejects a non-loopback
    ///         PEER outright, and neither check reads this setting.
    ///     </para>
    ///     <para>
    ///         Written through a dedicated in-memory provider rather than through the configuration indexer. The
    ///         indexer calls <c>Set</c> on every provider and a read then wins from the last provider holding the key,
    ///         so the widening would survive only while a non-reloading provider happened to sit after the JSON one.
    ///         <c>HostFilteringOptions</c> is monitor-backed and <c>appsettings.json</c> reloads on change, so that is
    ///         a reload away from silently reverting. One highest-precedence provider cannot be replaced by a reload.
    ///     </para>
    /// </summary>
    internal static void AllowBridgeHost(WebApplicationBuilder builder, ResolvedContainerBridgeEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(endpoint);

        var allowed = new List<string>();
        var configured = builder.Configuration[AllowedHostsKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            allowed.AddRange(configured.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        // Both spellings a container may present: the bound address a Linux container dials, and the alias a Docker
        // Desktop container dials, which arrives in the Host header as the name rather than as an address.
        allowed.AddRange(BridgeHostNames(endpoint).Where(host => !allowed.Contains(host, StringComparer.OrdinalIgnoreCase)));

        builder.Configuration.AddInMemoryCollection([new KeyValuePair<string, string?>(AllowedHostsKey, string.Join(';', allowed))]);
    }

    /// <summary>The host names a container can legitimately put in the Host header when calling this bridge.</summary>
    internal static IReadOnlyList<string> BridgeHostNames(ResolvedContainerBridgeEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var address = endpoint.BindAddress.ToString();
        var separator = endpoint.ContainerFacingEndpoint.LastIndexOf(':');
        var containerFacingHost = separator > 0 ? endpoint.ContainerFacingEndpoint[..separator] : endpoint.ContainerFacingEndpoint;

        return string.Equals(address, containerFacingHost, StringComparison.OrdinalIgnoreCase)
            ? [address]
            : [address, containerFacingHost];
    }

    /// <summary>
    ///     Maps the bridge branch. Called only when the bridge listener was actually opened, so
    ///     <paramref name="endpoint" /> always names an address and port Kestrel is bound to.
    /// </summary>
    internal static void Map(WebApplication app, ResolvedContainerBridgeEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(endpoint);

        app.MapWhen(context => IsBridgeConnection(context, endpoint),
            bridge =>
            {
                // Before anything else on this listener: a peer that is not this computer is refused without ever
                // presenting a credential, so a wrong token and a wrong machine are never the same answer.
                bridge.UseMiddleware<ContainerBridgePeerGuardMiddleware>();

                // Then the per-instance token, on EVERY bridge route. The peer guard admits any container on an
                // engine-owned network; this is what stops one of them using another's bridge.
                bridge.UseMiddleware<ContainerBridgeTokenMiddleware>();

                // The same forwarder the loopback model proxy uses, not a second copy of it: the five-step call
                // sequence it owns — model existence, supervisor, endpoint, inference lease, streamed forward with
                // an idle-read watchdog — is the whole reason a container's request is safe to serve.
                bridge.Run(HandleAsync);
            });
    }

    /// <summary>
    ///     Whether a connection arrived on the bridge listener. The discriminator is the socket's whole local end —
    ///     address AND port — because it is the one fact a caller cannot forge, and because the port alone is
    ///     ambiguous on a node whose loopback listener happens to carry the bridge's port.
    /// </summary>
    internal static bool IsBridgeConnection(HttpContext context, ResolvedContainerBridgeEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(endpoint);

        return endpoint.Matches(context.Connection.LocalIpAddress, context.Connection.LocalPort);
    }

    /// <summary>
    ///     The bridge's three routes, matched by path and method directly rather than through a routing branch: the
    ///     surface is three paths and a terminal, and a branch-local router would be more moving parts than the
    ///     thing it routes.
    /// </summary>
    private static Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var forwarder = context.RequestServices.GetRequiredService<LocalModelProxyForwarder>();

        if (path.Equals(ChatCompletionsPath, StringComparison.OrdinalIgnoreCase))
        {
            return HttpMethods.IsPost(context.Request.Method)
                ? forwarder.ForwardChatCompletionsAsync(context)
                : WriteMethodNotAllowedAsync(context, HttpMethods.Post);
        }

        if (path.Equals(EmbeddingsPath, StringComparison.OrdinalIgnoreCase))
        {
            return HttpMethods.IsPost(context.Request.Method)
                ? forwarder.ForwardEmbeddingsAsync(context)
                : WriteMethodNotAllowedAsync(context, HttpMethods.Post);
        }

        if (path.Equals(ModelsPath, StringComparison.OrdinalIgnoreCase))
        {
            return HttpMethods.IsGet(context.Request.Method)
                ? forwarder.WriteModelsAsync(context)
                : WriteMethodNotAllowedAsync(context, HttpMethods.Get);
        }

        return WriteNotFoundAsync(context);
    }

    private static async Task WriteMethodNotAllowedAsync(HttpContext context, string allowed)
    {
        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        context.Response.Headers[HeaderNames.Allow] = allowed;
        context.Response.ContentType = MediaTypeNames.Application.Json;
        await context.Response.WriteAsync(MethodNotAllowedBody, context.RequestAborted);
    }

    private static async Task WriteNotFoundAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = MediaTypeNames.Application.Json;
        await context.Response.WriteAsync(NotFoundBody, context.RequestAborted);
    }
}
